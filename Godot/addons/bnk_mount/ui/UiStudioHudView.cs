#if TOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Godot;
using Refii.EditorTools;
using Refii.Ui;

namespace Refii.Assets.UI;

/// <summary>
/// The UI tab's HUD page: the active mod's <c>ui/hud.json</c> — its own pieces (meter, panel,
/// picture, text), the game's pieces it hides, scales, copies and moves (the <c>layout</c> block,
/// through the shared <see cref="UiLayoutEditor"/>) — over a live preview: the game's own
/// <see cref="Engine_HudView"/> built from <c>hud.bgf</c> on the editor's mount, with the enabled
/// mods' files and this mod's file as edited composed in load order, stand-in values for every
/// meter and for each <c>mod:</c> key the pieces read, and drag-to-move.
/// </summary>
[Tool]
public partial class UiStudioHudView : VBoxContainer
{
	/// <summary>The presentation the HUD is cut from.</summary>
	private const string HudPresentation = "hud";

	private readonly UiStudioDocs _docs;
	private readonly UiStudioPreview _preview;

	/// <summary>The mod's HUD file changed (the tab refreshes its markers and the validator).</summary>
	public event Action? Edited;

	private ItemList _pieces = null!;
	private UiStudioFields _pieceFields = null!;
	private OptionButton _piecePreset = null!;
	private UiLayoutEditor _layout = null!;
	private CheckBox _hide = null!, _scaled = null!;
	private CheckBox? _snap;
	private SpinBox _scale = null!;
	private Button _copy = null!;
	private ItemList _clones = null!;
	private UiStudioFields _cloneFields = null!;
	private bool _filling;

	private bool _active;
	private Engine_HudView? _hud;
	private Engine_UiTheme? _hudBook;
	private Dictionary<string, Engine_HudModLayout.Move> _layoutSynced = new(StringComparer.Ordinal);
	private readonly Dictionary<string, float> _standIns = new(StringComparer.Ordinal);
	private float _meterStandIn = 0.75f;
	private VBoxContainer? _standInRows;
	private Control? _standInCard;
	private List<string> _standInKeys = new();

	private string? _dragId;
	private Vector2 _dragFrom;
	private readonly List<(Node3D Node, Vector3 From)> _dragNodes = new();

	public UiStudioHudView(UiStudioDocs docs, UiStudioPreview preview)
	{
		_docs = docs;
		_preview = preview;
	}

	public override void _Ready()
	{
		var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
		AddChild(scroll);
		var column = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		column.AddThemeConstantOverride("separation", 10);
		scroll.AddChild(column);

		column.AddChild(BuildPieces());
		column.AddChild(BuildShipped());
		column.AddChild(BuildClones());

		_preview.HostLoaded += () =>
		{
			if (_active)
			{
				LoadLayoutPieces();
				Rebuild();
			}
		};
		_preview.WindowChanged += () =>
		{
			if (_hud is not null && GodotObject.IsInstanceValid(_hud))
			{
				_hud.Window = _preview.WindowSize;
			}

			_layout.ReferenceWindow = _preview.WindowSize;
		};
	}

	// ---------------------------------------------------------------------------------------
	// The mod's own pieces
	// ---------------------------------------------------------------------------------------

	private static readonly string[] Kinds = { "meter", "panel", "picture", "text" };
	private static readonly string[] Boxed = { "meter", "panel", "picture" };

	private Control BuildPieces()
	{
		var body = new VBoxContainer();
		body.AddChild(ModStudioUiStyles.Note("Pieces of the mod's own, drawn with the HUD's own primitives and fonts. A piece is placed by " +
			"anchor + offset, or hangs under a game piece with follow. A value source is a number, mod:<key> (a value the mod's code " +
			"publishes) or hero:health / hero:health_max; a piece whose value is not stated is not drawn."));

		var buttons = new HFlowContainer();
		foreach (string kind in Kinds)
		{
			var add = new Button { Text = "+ " + char.ToUpperInvariant(kind[0]) + kind[1..] };
			add.Pressed += () => AddPiece(kind);
			buttons.AddChild(add);
		}

		var remove = new Button { Text = "🗑 Remove" };
		ModStudioUiStyles.ApplyRedButtonStyle(remove);
		remove.Pressed += RemovePiece;
		buttons.AddChild(remove);
		body.AddChild(buttons);

		_pieces = new ItemList { CustomMinimumSize = new Vector2(0, 110), AutoHeight = true };
		_pieces.ItemSelected += _ => ShowPiece();
		body.AddChild(_pieces);

		var fields = new VBoxContainer();
		body.AddChild(fields);
		_pieceFields = new UiStudioFields(fields);
		_pieceFields.Add("id", "Id", UiStudioFields.Type.Text, "Unique across mods; a higher-priority mod's piece of the same id replaces this one");
		_pieceFields.Add("kind", "Kind", UiStudioFields.Type.Choice, "meter: back, chip and fill; panel: back and border; picture: a texture; text: a line of the HUD's text", choices: Kinds);

		_piecePreset = new OptionButton();
		foreach (Engine_HudLayoutEdit.Preset preset in Engine_HudLayoutEdit.Presets)
		{
			_piecePreset.AddItem(preset.Label);
		}

		_piecePreset.AddItem("Custom");
		_piecePreset.ItemSelected += OnPiecePreset;
		fields.AddChild(ModStudioUiStyles.Field("Anchor preset", _piecePreset, "Re-anchors the piece and keeps it where it stands at the preview's window"));
		_pieceFields.Add("anchor", "Anchor", UiStudioFields.Type.Pair, "Screen fraction 0..1 each way the offset is measured from");
		_pieceFields.Add("offset", "Offset", UiStudioFields.Type.Pair, "Design units from the anchor (+Y down); from the followed element's bottom-left with follow");
		_pieceFields.Add("follow", "Follow", UiStudioFields.Type.Text, "A game piece id to hang beneath as it is drawn; a comma list, the first drawn wins");
		_pieceFields.Add("followElement", "Follow element", UiStudioFields.Type.Text, "An element (or comma list) inside the followed piece");
		_pieceFields.Add("size", "Size", UiStudioFields.Type.Pair, "Width, height in design units; width 0 takes the followed element's", Boxed);
		_pieceFields.Add("size", "Point size", UiStudioFields.Type.Number, $"The text's point size ({Engine_HudModText.DefaultSize} when empty)", new[] { "text" });
		_pieceFields.Add("text", "Text", UiStudioFields.Type.Text, "A literal, a Babel tag, or mod:<key> drawn through format", new[] { "text" });
		_pieceFields.Add("format", "Format", UiStudioFields.Type.Text, "For mod:<key>: \"Stamina {0:0}\" or a numeric format like 0.0", new[] { "text" });
		_pieceFields.Add("colour", "Colour", UiStudioFields.Type.Colour, "The text's colour (the HUD text's own when empty)", new[] { "text" });
		_pieceFields.Add("align", "Align", UiStudioFields.Type.Choice, "Which edge of the text sits at the piece's point", new[] { "text" }, new[] { "left", "centre", "right" });
		_pieceFields.Add("value", "Value", UiStudioFields.Type.Text, "The value source the piece draws", Boxed);
		_pieceFields.Add("max", "Max", UiStudioFields.Type.Text, "A number or a value source (1 when empty)", new[] { "meter" });
		_pieceFields.Add("fill", "Fill", UiStudioFields.Type.Colour, "The bar's colour", new[] { "meter" });
		_pieceFields.Add("back", "Back", UiStudioFields.Type.Colour, "The plate behind the bar", Boxed);
		_pieceFields.Add("chip", "Chip", UiStudioFields.Type.Colour, "The recently-lost part of the bar, held then drained", new[] { "meter" });
		_pieceFields.Add("border", "Border", UiStudioFields.Type.Colour, "An outline", Boxed);
		_pieceFields.Add("borderWidth", "Border width", UiStudioFields.Type.Number, "The outline's width", Boxed);
		_pieceFields.Add("corner", "Corner", UiStudioFields.Type.Number, "The corner radius", Boxed);
		_pieceFields.Add("widthFrom", "Width from", UiStudioFields.Type.Text, "A value source whose value sets the bar's length", new[] { "meter" });
		_pieceFields.Add("widthPerUnit", "Width per unit", UiStudioFields.Type.Number, "Design units of length per unit of widthFrom", new[] { "meter" });
		_pieceFields.Add("minWidth", "Min width", UiStudioFields.Type.Number, "The shortest the bar gets", new[] { "meter" });
		_pieceFields.Add("maxWidth", "Max width", UiStudioFields.Type.Number, "The longest the bar gets", new[] { "meter" });
		_pieceFields.Add("texture", "Texture", UiStudioFields.Type.Text, "game://art/... .tex or a res:// image", new[] { "picture" });
		_pieceFields.Add("textures", "Textures", UiStudioFields.Type.List, "A list of textures; index picks one", new[] { "picture" });
		_pieceFields.Add("index", "Index", UiStudioFields.Type.Text, "A value source whose integer part picks from textures (-1: none)", new[] { "picture" });
		_pieceFields.Changed += key =>
		{
			if (key is "kind" or "id")
			{
				FillPieces(Selected(_pieces));
			}

			if (key == "anchor")
			{
				ShowPiecePreset();
			}

			HudEdited();
		};

		return ModStudioUiStyles.Card("🧩 The mod's pieces", new Color(0.55f, 0.85f, 1.0f), body);
	}

	private JsonArray List(string key)
	{
		if (_docs.Hud[key] is not JsonArray list)
		{
			list = new JsonArray();
			_docs.Hud[key] = list;
		}

		return list;
	}

	private static string? Selected(ItemList list) =>
		list.GetSelectedItems() is { Length: > 0 } selected ? list.GetItemMetadata(selected[0]).AsString() : null;

	private JsonObject? Record(string listKey, string? id) =>
		id is null ? null : List(listKey).OfType<JsonObject>().FirstOrDefault(r => Engine_UiMenuEdit.Text(r, "id") == id);

	private void FillPieces(string? select)
	{
		_filling = true;
		_pieces.Clear();
		foreach (JsonObject piece in List(Engine_HudModEdit.PiecesKey).OfType<JsonObject>())
		{
			string id = Engine_UiMenuEdit.Text(piece, "id");
			string kind = Engine_UiMenuEdit.Text(piece, "kind");
			int at = _pieces.AddItem($"{id}   ({(kind.Length == 0 ? "meter" : kind)})");
			_pieces.SetItemMetadata(at, id);
			if (id == select)
			{
				_pieces.Select(at);
			}
		}

		_filling = false;
		ShowPiece();
	}

	private void ShowPiece()
	{
		if (_filling)
		{
			return;
		}

		JsonObject? piece = Record(Engine_HudModEdit.PiecesKey, Selected(_pieces));
		string kind = piece is null ? string.Empty : Engine_UiMenuEdit.Text(piece, "kind");
		_pieceFields.Bind(piece, kind.Length == 0 ? "meter" : kind);
		_piecePreset.GetParent<Control>().Visible = piece is not null;
		ShowPiecePreset();
	}

	private void ShowPiecePreset()
	{
		if (_pieceFields.Record is not { } piece)
		{
			return;
		}

		Engine_HudModLayout.Move move = PieceMove(piece);
		int preset = Engine_HudLayoutEdit.PresetOf(move.AnchorX, move.AnchorY);
		_filling = true;
		_piecePreset.Select(preset < 0 ? Engine_HudLayoutEdit.Presets.Length : preset);
		_filling = false;
	}

	private static Engine_HudModLayout.Move PieceMove(JsonObject piece)
	{
		(float ax, float ay) = Pair(piece, "anchor");
		(float ox, float oy) = Pair(piece, "offset");
		return new Engine_HudModLayout.Move(ax, ay, ox, oy);
	}

	private static (float X, float Y) Pair(JsonObject record, string key) =>
		record[key] is JsonArray { Count: 2 } pair
			? ((float)(Engine_UiMenuEdit.NumberOf(pair[0]) ?? 0), (float)(Engine_UiMenuEdit.NumberOf(pair[1]) ?? 0))
			: (0f, 0f);

	private static JsonArray PairOf(float x, float y) =>
		new(JsonValue.Create(Math.Round(x, 1)), JsonValue.Create(Math.Round(y, 1)));

	private void SetPieceMove(JsonObject piece, Engine_HudModLayout.Move move)
	{
		piece["anchor"] = PairOf(move.AnchorX, move.AnchorY);
		piece["offset"] = PairOf(move.OffsetX, move.OffsetY);
	}

	private void OnPiecePreset(long index)
	{
		if (_filling || _pieceFields.Record is not { } piece || index >= Engine_HudLayoutEdit.Presets.Length)
		{
			return;
		}

		Engine_HudLayoutEdit.Preset preset = Engine_HudLayoutEdit.Presets[index];
		Vector2 extent = Engine_HudLayout.CanvasExtent(_preview.WindowSize);
		SetPieceMove(piece, Engine_HudLayoutEdit.Rebase(PieceMove(piece), preset.X, preset.Y, extent.X, extent.Y,
			Engine_HudLayout.Scale(extent)));
		ShowPiece();
		HudEdited();
	}

	private void AddPiece(string kind)
	{
		string prefix = _docs.Mod.Length == 0 ? kind : _docs.Mod + "." + kind;
		string id = prefix;
		for (int n = 2; Record(Engine_HudModEdit.PiecesKey, id) is not null; n++)
		{
			id = prefix + n.ToString(CultureInfo.InvariantCulture);
		}

		var piece = new JsonObject
		{
			["id"] = id,
			["kind"] = kind,
			["anchor"] = PairOf(0.5f, 0.5f),
			["offset"] = PairOf(0f, 0f),
		};
		switch (kind)
		{
			case "text":
				piece["text"] = "New text";
				piece["size"] = Engine_HudModText.DefaultSize;
				break;
			case "meter":
				piece["size"] = PairOf(200f, 10f);
				piece["value"] = $"mod:{_docs.Mod}.value";
				piece["max"] = "100";
				break;
			default:
				piece["size"] = PairOf(64f, 64f);
				break;
		}

		List(Engine_HudModEdit.PiecesKey).Add(piece);
		FillPieces(id);
		HudEdited();
	}

	private void RemovePiece()
	{
		if (Record(Engine_HudModEdit.PiecesKey, Selected(_pieces)) is not { } piece)
		{
			return;
		}

		List(Engine_HudModEdit.PiecesKey).Remove(piece);
		FillPieces(null);
		HudEdited();
	}

	// ---------------------------------------------------------------------------------------
	// The game's pieces: move (layout), hide, scale, copy
	// ---------------------------------------------------------------------------------------

	private Control BuildShipped()
	{
		var body = new VBoxContainer();
		body.AddChild(ModStudioUiStyles.Note("The game's own pieces, cut from hud.bgf. Moving one writes the mod's \"layout\" block — the " +
			"entry shape of the player's user://hud/layout.json, which still wins over it. Drag a piece in the preview, or set it here."));
		_layout = new UiLayoutEditor { TargetName = "this mod", CustomMinimumSize = new Vector2(0, 260) };
		_layout.Edited += SyncLayout;
		_layout.PieceSelected += ShowShipped;
		body.AddChild(_layout);

		var row = new HFlowContainer();
		_hide = new CheckBox { Text = "Hide (the mod replaces it)" };
		_hide.Toggled += OnHide;
		row.AddChild(_hide);
		_scaled = new CheckBox { Text = "Scale" };
		_scaled.Toggled += _ => OnScale();
		row.AddChild(_scaled);
		_scale = new SpinBox { MinValue = 0.05, MaxValue = 8, Step = 0.05, Value = 1, AllowGreater = true };
		_scale.ValueChanged += _ => OnScale();
		row.AddChild(_scale);
		_copy = new Button { Text = "+ Copy this piece", TooltipText = "A copy of the game's widget beside it, recoloured or fed by mod values" };
		_copy.Pressed += CopyShipped;
		row.AddChild(_copy);
		body.AddChild(row);

		return ModStudioUiStyles.Card("🎯 The game's pieces", new Color(0.95f, 0.80f, 0.45f), body);
	}

	private void ShowShipped(string id)
	{
		_filling = true;
		_hide.ButtonPressed = List(Engine_HudModEdit.HideKey).Any(n => n is JsonValue v && v.TryGetValue(out string? s) && s == id);
		double? scale = _docs.Hud[Engine_HudModEdit.ScaleKey] is JsonObject scales ? Engine_UiMenuEdit.NumberOf(scales[id]) : null;
		_scaled.ButtonPressed = scale is not null;
		_scale.Value = scale ?? 1.0;
		_filling = false;
	}

	private void OnHide(bool hidden)
	{
		if (_filling || _layout.SelectedId is not { } id)
		{
			return;
		}

		JsonArray list = List(Engine_HudModEdit.HideKey);
		foreach (JsonNode? node in list.Where(n => n is JsonValue v && v.TryGetValue(out string? s) && s == id).ToList())
		{
			list.Remove(node);
		}

		if (hidden)
		{
			list.Add(id);
		}

		if (list.Count == 0)
		{
			_docs.Hud.Remove(Engine_HudModEdit.HideKey);
		}

		HudEdited();
	}

	private void OnScale()
	{
		if (_filling || _layout.SelectedId is not { } id)
		{
			return;
		}

		UiStudioFields.Set(_docs.Hud, Engine_HudModEdit.ScaleKey + "." + id,
			_scaled.ButtonPressed ? JsonValue.Create(Math.Round(_scale.Value, 3)) : null);
		HudEdited();
	}

	private void CopyShipped()
	{
		if (_layout.SelectedId is not { } of)
		{
			return;
		}

		string tail = of.Split('.').Last();
		string prefix = (_docs.Mod.Length == 0 ? string.Empty : _docs.Mod + ".") + tail + "_copy";
		string id = prefix;
		for (int n = 2; Record(Engine_HudModEdit.ClonesKey, id) is not null; n++)
		{
			id = prefix + n.ToString(CultureInfo.InvariantCulture);
		}

		List(Engine_HudModEdit.ClonesKey).Add(new JsonObject { ["id"] = id, ["of"] = of, ["offset"] = PairOf(0f, 34f) });
		FillClones(id);
		HudEdited();
	}

	/// <summary>The layout editor's entries into the file's <c>layout</c> block (only what changed).</summary>
	private void SyncLayout()
	{
		var stated = new Dictionary<string, Engine_HudModLayout.Move>(_layout.Stated, StringComparer.Ordinal);
		Engine_HudLayoutEdit.Changes changes = Engine_HudLayoutEdit.Diff(_layoutSynced, stated);
		_layoutSynced = stated;
		if (changes.Empty)
		{
			return;
		}

		Engine_HudLayoutEdit.Apply(_docs.Hud, Engine_HudModLayout.Section, changes);
		if (_docs.Hud[Engine_HudModLayout.Section] is JsonObject { Count: 0 })
		{
			_docs.Hud.Remove(Engine_HudModLayout.Section);
		}

		HudEdited();
	}

	/// <summary>
	/// The game's pieces for the layout editor, each at the place beneath this mod (the shipped
	/// arrangement with the OTHER enabled mods' layout over it): from <c>hud.bgf</c> once the preview
	/// has loaded, else from the last run's snapshot.
	/// </summary>
	private void LoadLayoutPieces()
	{
		var pieces = new List<HudLayoutPiece>();
		Engine_HudModLayout.Composed others = Engine_HudModLayout.Compose(_docs.Files(Engine_HudModPieces.ModHudPath, null));
		if (ShippedLayout() is { } layout)
		{
			layout.ApplyMoves(others.Moves, "the other mods' layout");
			foreach (Engine_HudLayout.Piece piece in layout.Pieces)
			{
				pieces.Add(new HudLayoutPiece(piece.Id,
					new Engine_HudModLayout.Move(piece.Anchor.X, piece.Anchor.Y, piece.Offset.X, piece.Offset.Y),
					$"art: {piece.Art}   value: {piece.Value}   drive: {piece.Driven}" + (piece.Placed ? string.Empty : "   (not placed in the shipped layout — placing it draws it)")));
			}
		}
		else if (PlayerHudLayoutTarget.Load() is { } snapshot)
		{
			pieces.AddRange(snapshot.Pieces);
		}

		_layoutSynced = Engine_HudModLayout.ParseModFile(UiStudioDocs.Text(_docs.Hud));
		_layout.Load(pieces, _layoutSynced);
		if (pieces.Count == 0)
		{
			_layout.SetStatus("The game's pieces come from hud.bgf once the preview loads (or from a run's snapshot).");
		}
	}

	private Engine_HudLayout? ShippedLayout()
	{
		if (!_preview.Loaded || _preview.Host!.DocumentFor(HudPresentation) is not { } screen)
		{
			return null;
		}

		return Engine_HudLayout.FromBindings(screen, Engine_UiBindings.Read(screen.Document), Engine_UiWidescreen.Read(screen.Document));
	}

	// ---------------------------------------------------------------------------------------
	// Copies of the game's pieces
	// ---------------------------------------------------------------------------------------

	private Control BuildClones()
	{
		var body = new VBoxContainer();
		body.AddChild(ModStudioUiStyles.Note("A copy is the game's own widget built again beside the original — it moves, fades and hides " +
			"with it — recoloured (hue), re-iconed and fed by mod values (values: meter=source or meter=source/cap; bars: element=meter)."));
		_clones = new ItemList { CustomMinimumSize = new Vector2(0, 70), AutoHeight = true };
		_clones.ItemSelected += _ => ShowClone();
		body.AddChild(_clones);
		var remove = new Button { Text = "🗑 Remove copy" };
		ModStudioUiStyles.ApplyRedButtonStyle(remove);
		remove.Pressed += () =>
		{
			if (Record(Engine_HudModEdit.ClonesKey, Selected(_clones)) is { } clone)
			{
				List(Engine_HudModEdit.ClonesKey).Remove(clone);
				FillClones(null);
				HudEdited();
			}
		};
		body.AddChild(remove);

		var fields = new VBoxContainer();
		body.AddChild(fields);
		_cloneFields = new UiStudioFields(fields);
		_cloneFields.Add("id", "Id", UiStudioFields.Type.Text, "The copy's own id");
		_cloneFields.Add("of", "Of", UiStudioFields.Type.Text, "The game piece copied");
		_cloneFields.Add("offset", "Offset", UiStudioFields.Type.Pair, "From the original, design units (+Y down)");
		_cloneFields.Add("hue", "Hue", UiStudioFields.Type.Number, "A hue rotation 0..1 of the named elements' art");
		_cloneFields.Add("hueElements", "Hue elements", UiStudioFields.Type.List, "The elements the hue applies to");
		_cloneFields.Add("icon.element", "Icon element", UiStudioFields.Type.Text, "The element whose picture is replaced");
		_cloneFields.Add("icon.textures", "Icon textures", UiStudioFields.Type.List, "Candidates; the first that loads");
		_cloneFields.Add("values", "Values", UiStudioFields.Type.Map, "meter=source or meter=source/cap; \"none\" holds a meter still");
		_cloneFields.Add("bars", "Bars", UiStudioFields.Type.Map, "element=meter: the elements a meter fills");
		_cloneFields.Changed += key =>
		{
			if (key == "id")
			{
				FillClones(Selected(_clones));
			}

			HudEdited();
		};

		return ModStudioUiStyles.Card("🪞 Copies of the game's pieces", new Color(0.70f, 0.85f, 0.65f), body);
	}

	private void FillClones(string? select)
	{
		_filling = true;
		_clones.Clear();
		foreach (JsonObject clone in List(Engine_HudModEdit.ClonesKey).OfType<JsonObject>())
		{
			string id = Engine_UiMenuEdit.Text(clone, "id");
			int at = _clones.AddItem($"{id}   (of {Engine_UiMenuEdit.Text(clone, "of")})");
			_clones.SetItemMetadata(at, id);
			if (id == select)
			{
				_clones.Select(at);
			}
		}

		_filling = false;
		ShowClone();
	}

	private void ShowClone()
	{
		if (!_filling)
		{
			_cloneFields.Bind(Record(Engine_HudModEdit.ClonesKey, Selected(_clones)));
		}
	}

	// ---------------------------------------------------------------------------------------
	// Load, edits, the tab's hooks
	// ---------------------------------------------------------------------------------------

	/// <summary>Shows the mod's file as the docs hold it.</summary>
	public void Reload()
	{
		if (!IsNodeReady())
		{
			return;
		}

		FillPieces(null);
		FillClones(null);
		LoadLayoutPieces();
		if (_active)
		{
			Rebuild();
		}
	}

	private void HudEdited()
	{
		Rebuild();
		Edited?.Invoke();
	}

	/// <summary>The page is up: its preview stands on the host.</summary>
	public void Activate()
	{
		_active = true;
		_preview.EnsureHost();
		BuildStandIns();
		if (_preview.Loaded)
		{
			LoadLayoutPieces();
			Rebuild();
		}

		_preview.Pressed += OnPressed;
		_preview.Dragged += OnDragged;
		_preview.Released += OnReleased;
	}

	public void Deactivate()
	{
		_active = false;
		_preview.Pressed -= OnPressed;
		_preview.Dragged -= OnDragged;
		_preview.Released -= OnReleased;
		FreeHud();
		foreach (Node child in _preview.Below.GetChildren())
		{
			child.QueueFree();
		}

		foreach (Node child in _preview.Toolbar.GetChildren())
		{
			child.QueueFree();
		}

		_standInRows = null;
		_standInCard = null;
		_snap = null;
	}

	/// <summary>The validator's issues and the load order's conflicts for <c>ui/hud.json</c>.</summary>
	public IEnumerable<(bool Error, string Text)> Issues()
	{
		string text = UiStudioDocs.Text(_docs.Hud);
		foreach (Refii.Mods.ModIssue issue in Refii.Mods.ModValidate.Validate(Engine_HudModPieces.ModHudPath, text))
		{
			yield return (issue.Error, issue.ToString());
		}

		List<(string Mod, string Text)> files = _docs.Files(Engine_HudModPieces.ModHudPath, text);
		foreach (Engine_HudModLayout.Conflict conflict in Engine_HudModLayout.Compose(files).Conflicts)
		{
			if (string.Equals(conflict.Winner, _docs.Mod, StringComparison.OrdinalIgnoreCase))
			{
				yield return (false, $"layout '{conflict.Piece}': this mod's move wins over mod '{conflict.Loser}'");
			}
			else if (string.Equals(conflict.Loser, _docs.Mod, StringComparison.OrdinalIgnoreCase))
			{
				yield return (false, $"layout '{conflict.Piece}': mod '{conflict.Winner}' (higher in the load order) moves it instead");
			}
		}

		var ours = new HashSet<string>(Engine_HudModEdit.ById(_docs.Hud, Engine_HudModEdit.PiecesKey).Keys
			.Concat(Engine_HudModEdit.ById(_docs.Hud, Engine_HudModEdit.ClonesKey).Keys), StringComparer.Ordinal);
		foreach ((string mod, string other) in files.Where(f => !string.Equals(f.Mod, _docs.Mod, StringComparison.OrdinalIgnoreCase)))
		{
			JsonObject theirs = Engine_HudLayoutEdit.ParseObject(other);
			foreach (string id in Engine_HudModEdit.ById(theirs, Engine_HudModEdit.PiecesKey).Keys
				.Concat(Engine_HudModEdit.ById(theirs, Engine_HudModEdit.ClonesKey).Keys).Where(ours.Contains))
			{
				yield return (false, $"piece '{id}' is also stated by mod '{mod}'; the higher one in the load order is drawn");
			}
		}
	}

	// ---------------------------------------------------------------------------------------
	// The preview
	// ---------------------------------------------------------------------------------------

	private void FreeHud()
	{
		if (_hud is not null && GodotObject.IsInstanceValid(_hud))
		{
			_hud.QueueFree();
		}

		_hud = null;
		_dragNodes.Clear();
		_dragId = null;
	}

	/// <summary>
	/// Builds the game's HUD view again with the mod's file as it stands: the shipped arrangement,
	/// the enabled mods' and this mod's layout composed in load order, every piece stood in.
	/// </summary>
	private void Rebuild()
	{
		if (!_active || !_preview.Loaded)
		{
			return;
		}

		FreeHud();
		UiStudioPreviewHost host = _preview.Host!;
		if (host.DocumentFor(HudPresentation) is not { } screen)
		{
			_preview.SetStatus($"'{HudPresentation}' is not in the banks; no HUD to preview.");
			return;
		}

		List<(string Mod, string Text)> files = _docs.Files(Engine_HudModPieces.ModHudPath, UiStudioDocs.Text(_docs.Hud));
		Engine_HudLayout layout = Engine_HudLayout.FromBindings(screen, Engine_UiBindings.Read(screen.Document), Engine_UiWidescreen.Read(screen.Document));
		Engine_HudModLayout.Composed composed = Engine_HudModLayout.Compose(files);
		if (composed.Moves.Count != 0)
		{
			layout.ApplyMoves(composed.Moves, "the Studio's preview");
		}

		_hudBook ??= host.PatternBookFor(HudPresentation);
		var active = new bool[screen.Nodes.Length];
		Array.Fill(active, true);
		_hud = new Engine_HudView
		{
			Name = "StudioHud",
			Theme = host.Theme,
			Screen = screen,
			Art = new Engine_HudArt { Shipped = _hudBook },
			ResolveTexture = hash => host.TextureNames is { } names && names.TryResolve(hash, out string path) ? host.LoadTexture(path) : null,
			Layout = layout,
			ReadsValues = false,
			ModFiles = files,
			Window = _preview.WindowSize,
			ElementActive = active,
		};
		host.AddChild(_hud);
		_hud.Values.Strings = host.Strings;

		// Stand-ins for what the guest would publish: every meter at the meters' stand-in, every
		// text field its own value name.
		foreach (Engine_HudLayout.Piece piece in layout.Pieces)
		{
			if (piece.Value.Length == 0)
			{
				continue;
			}

			var handle = Engine_HudValues.Handle.For(piece.Value);
			if (piece.Driven == Engine_HudLayout.Drive.Text)
			{
				_hud.Values.Override(handle, piece.Value);
			}
			else
			{
				_hud.Values.OverrideMeter(handle, _meterStandIn * 100f);
			}
		}

		_hud.Build();
		if (_hud.ModPieces is { } mods)
		{
			mods.PreviewValue = _meterStandIn;
			mods.PreviewSource = key => _standIns.TryGetValue(key, out float value) ? value : null;
		}

		BuildStandIns();
	}

	/// <summary>The stand-in sliders: the game's meters, and one per <c>mod:</c> key the file reads.</summary>
	private void BuildStandIns()
	{
		if (!_active)
		{
			return;
		}

		List<string> keys = Engine_HudModEdit.ModKeys(_docs.Hud);
		if (_standInRows is not null && GodotObject.IsInstanceValid(_standInRows) && keys.SequenceEqual(_standInKeys))
		{
			return;
		}

		_standInKeys = keys;
		if (_standInCard is not null && GodotObject.IsInstanceValid(_standInCard))
		{
			_standInCard.QueueFree();
		}

		if (_snap is null || !GodotObject.IsInstanceValid(_snap))
		{
			_snap = new CheckBox { Text = "Snap anchor on drop", ButtonPressed = true, TooltipText = "A dropped piece takes the anchor nearest where it lands, so it follows that edge on other windows" };
			_preview.Toolbar.AddChild(_snap);
		}

		_standInRows = new VBoxContainer();
		_standInRows.AddChild(StandIn("Game meters (0..1)", _meterStandIn, 0, 1, 0.01, value =>
		{
			_meterStandIn = value;
			Rebuild();
		}));
		foreach (string key in keys)
		{
			float start = _standIns.TryGetValue(key, out float known) ? known : 75f;
			_standIns[key] = start;
			_standInRows.AddChild(StandIn("mod:" + key, start, 0, 100, 0.1, value => _standIns[key] = value));
		}

		_standInCard = ModStudioUiStyles.Card("Stand-in values", new Color(0.55f, 0.85f, 1.0f), _standInRows);
		_preview.Below.AddChild(_standInCard);
	}

	private static HBoxContainer StandIn(string caption, float start, double min, double max, double step, Action<float> changed)
	{
		var slider = new HSlider { MinValue = min, MaxValue = max, Step = step, Value = start, SizeFlagsHorizontal = SizeFlags.ExpandFill, AllowGreater = true };
		var number = new SpinBox { MinValue = min, MaxValue = max, Step = step, Value = start, AllowGreater = true, AllowLesser = true };
		slider.ValueChanged += value =>
		{
			number.SetValueNoSignal(value);
			changed((float)value);
		};
		number.ValueChanged += value =>
		{
			slider.SetValueNoSignal(value);
			changed((float)value);
		};
		var row = new HBoxContainer();
		row.AddChild(slider);
		row.AddChild(number);
		return ModStudioUiStyles.Field(caption, row);
	}

	// ---- picking and dragging ---------------------------------------------------------------

	private enum PieceSource
	{
		Shipped,
		Mod,
		Clone,
	}

	/// <summary>The piece drawn under a canvas point: a mod piece first, then the smallest game piece or copy.</summary>
	private (string Id, PieceSource Source)? PieceAt(Vector2 canvas)
	{
		if (_hud is null || !GodotObject.IsInstanceValid(_hud))
		{
			return null;
		}

		(string Id, PieceSource Source)? best = null;
		float bestArea = float.MaxValue;
		if (_hud.ModPieces is { } mods)
		{
			foreach ((string id, Node3D node) in mods.DrawnNodes())
			{
				Consider(id, PieceSource.Mod, node, -1f);
			}
		}

		if (best is not null)
		{
			return best;
		}

		var clones = new HashSet<string>(Engine_HudModEdit.ById(_docs.Hud, Engine_HudModEdit.ClonesKey).Keys, StringComparer.Ordinal);
		foreach ((string id, Node3D node) in _hud.DrawnPieces())
		{
			Consider(id, clones.Contains(id) ? PieceSource.Clone : PieceSource.Shipped, node, 0f);
		}

		return best;

		void Consider(string id, PieceSource owner, Node3D node, float bias)
		{
			if (Engine_UiPointer.CanvasBounds(node) is { } bounds && bounds.HasPoint(canvas))
			{
				float area = bounds.Size.X * bounds.Size.Y + bias;
				if (area < bestArea)
				{
					bestArea = area;
					best = (id, owner);
				}
			}
		}
	}

	private void OnPressed(Vector2 pixel, MouseButton button)
	{
		if (button != MouseButton.Left || _preview.CanvasAt(pixel) is not { } canvas || PieceAt(canvas) is not { } hit)
		{
			_dragId = null;
			return;
		}

		_dragId = hit.Id;
		_dragOwner = hit.Source;
		_dragFrom = canvas;
		_dragNodes.Clear();
		IEnumerable<(string Id, Node3D Node)> nodes = hit.Source == PieceSource.Mod
			? _hud!.ModPieces?.DrawnNodes() ?? Enumerable.Empty<(string, Node3D)>()
			: _hud!.DrawnPieces();
		foreach ((string id, Node3D node) in nodes)
		{
			if (id == hit.Id)
			{
				_dragNodes.Add((node, node.Position));
			}
		}

		switch (hit.Source)
		{
			case PieceSource.Mod:
				FillPieces(hit.Id);
				break;
			case PieceSource.Clone:
				FillClones(hit.Id);
				break;
			default:
				_layout.Select(hit.Id);
				break;
		}

		_preview.SetStatus($"'{hit.Id}' — drag to move it.");
	}

	private PieceSource _dragOwner;

	private void OnDragged(Vector2 pixel)
	{
		if (_dragId is null || _preview.CanvasAt(pixel) is not { } canvas)
		{
			return;
		}

		Vector2 delta = canvas - _dragFrom;
		foreach ((Node3D node, Vector3 from) in _dragNodes)
		{
			if (GodotObject.IsInstanceValid(node))
			{
				node.Position = from + new Vector3(delta.X, delta.Y, 0f);
			}
		}
	}

	private void OnReleased(Vector2 pixel)
	{
		if (_dragId is not { } id || _preview.CanvasAt(pixel) is not { } canvas)
		{
			_dragId = null;
			return;
		}

		_dragId = null;
		Vector2 delta = canvas - _dragFrom;
		if (delta.Length() < 0.5f)
		{
			return;
		}

		Vector2 extent = Engine_HudLayout.CanvasExtent(_preview.WindowSize);
		float scale = Engine_HudLayout.Scale(extent);
		bool snap = _snap is not null && GodotObject.IsInstanceValid(_snap) && _snap.ButtonPressed;

		// Canvas +Y is up; a layout's offset is +Y down.
		switch (_dragOwner)
		{
			case PieceSource.Shipped:
			{
				Engine_HudModLayout.Move move = Engine_HudLayoutEdit.Drag(_layout.PlaceOf(id), delta.X, -delta.Y, scale);
				if (snap)
				{
					move = Engine_HudLayoutEdit.Snap(move, extent.X, extent.Y, scale);
				}

				_layout.SetPlace(id, Round(move));
				break;
			}

			case PieceSource.Mod when Record(Engine_HudModEdit.PiecesKey, id) is { } piece:
			{
				Engine_HudModLayout.Move move = Engine_HudLayoutEdit.Drag(PieceMove(piece), delta.X, -delta.Y, scale);
				if (snap && Engine_UiMenuEdit.Text(piece, "follow").Length == 0)
				{
					move = Engine_HudLayoutEdit.Snap(move, extent.X, extent.Y, scale);
				}

				SetPieceMove(piece, move);
				FillPieces(id);
				HudEdited();
				break;
			}

			case PieceSource.Clone when Record(Engine_HudModEdit.ClonesKey, id) is { } clone:
			{
				(float ox, float oy) = Pair(clone, "offset");
				clone["offset"] = PairOf(ox + delta.X / scale, oy - delta.Y / scale);
				FillClones(id);
				HudEdited();
				break;
			}
		}
	}

	private static Engine_HudModLayout.Move Round(Engine_HudModLayout.Move move) => new(
		(float)Math.Round(move.AnchorX, 3), (float)Math.Round(move.AnchorY, 3),
		(float)Math.Round(move.OffsetX, 1), (float)Math.Round(move.OffsetY, 1));
}
#endif
