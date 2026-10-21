#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;
using Refii.EditorTools;
using Refii.Ui;

namespace Refii.Assets.UI;

/// <summary>
/// The UI tab's Menus page: the shared <see cref="UiMenuEditor"/> over the mod's menus
/// (<see cref="ModMenuTarget"/>: its own menus whole, every other menu as a patch), the screens it
/// states (whole records), and a preview through the game's own list view
/// (<see cref="Engine_FrontEndMenuView"/>) built from the front end's or the pause book's pattern
/// art the way the game builds it — clicked to highlight and press, so an <c>opens</c> or an
/// <c>Opens:</c> row opens its menu over the list and Back returns.
/// </summary>
[Tool]
public partial class UiStudioMenusView : VBoxContainer
{
	private const string FrontEndPresentation = "frontendmainmenu";
	private const string PausePresentation = "pausemainmenu";

	private readonly UiStudioDocs _docs;
	private readonly UiStudioPreview _preview;

	/// <summary>A menu, a screen or the templates file changed.</summary>
	public event Action? Edited;

	public UiMenuEditor Editor { get; private set; } = null!;

	private OptionButton _screen = null!;
	private Label _screenNote = null!;
	private TextEdit _screenText = null!;
	private Button _stateScreen = null!, _dropScreen = null!;
	private bool _filling;

	private bool _active;
	private Engine_FrontEndMenuView? _view;
	private OptionButton? _look;
	private Label? _trail;
	private string? _root;

	public UiStudioMenusView(UiStudioDocs docs, UiStudioPreview preview)
	{
		_docs = docs;
		_preview = preview;
	}

	public override void _Ready()
	{
		var split = new VSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
		AddChild(split);

		Editor = new UiMenuEditor { SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 420) };
		Editor.Edited += (_, _) =>
		{
			BuildView();
			Edited?.Invoke();
		};
		Editor.MenuShown += _ => BuildView();
		Editor.Saved += () => Edited?.Invoke();
		split.AddChild(Editor);
		split.AddChild(BuildScreens());

		_preview.HostLoaded += () =>
		{
			if (_active)
			{
				BuildView();
			}
		};
	}

	/// <summary>Lists the mod's menus and screens as the docs hold them; unsaved menu edits are dropped.</summary>
	public void Reload()
	{
		if (!IsNodeReady())
		{
			return;
		}

		Editor.Bind(_docs.Menus);
		FillScreens();
	}

	// ---------------------------------------------------------------------------------------
	// Screens
	// ---------------------------------------------------------------------------------------

	private Control BuildScreens()
	{
		var body = new VBoxContainer();
		body.AddChild(ModStudioUiStyles.Note("A screen record is replaced whole: state one in the mod to start from the record as it runs, " +
			"then edit it. Dropping it lets the game's own (or a lower mod's) stand again."));
		var row = new HBoxContainer();
		_screen = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		_screen.ItemSelected += _ => ShowScreen();
		row.AddChild(_screen);
		_stateScreen = new Button { Text = "+ State in the mod" };
		_stateScreen.Pressed += StateScreen;
		row.AddChild(_stateScreen);
		_dropScreen = new Button { Text = "↺ Drop the mod's record" };
		ModStudioUiStyles.ApplyRedButtonStyle(_dropScreen);
		_dropScreen.Pressed += DropScreen;
		row.AddChild(_dropScreen);
		body.AddChild(row);
		_screenNote = ModStudioUiStyles.Note(string.Empty);
		body.AddChild(_screenNote);
		_screenText = new TextEdit { CustomMinimumSize = new Vector2(0, 160), SizeFlagsVertical = SizeFlags.ExpandFill };
		_screenText.TextChanged += OnScreenText;
		body.AddChild(_screenText);
		return ModStudioUiStyles.Card("🪟 Screens", new Color(0.70f, 0.75f, 1.0f), body);
	}

	private JsonObject ModScreens()
	{
		if (_docs.Templates["screens"] is not JsonObject screens)
		{
			screens = new JsonObject();
			_docs.Templates["screens"] = screens;
		}

		return screens;
	}

	private void FillScreens()
	{
		string? keep = _screen.Selected >= 0 ? (string)_screen.GetItemMetadata(_screen.Selected) : null;
		_filling = true;
		_screen.Clear();
		IEnumerable<string> ids = _docs.Menus.Screens.Select(p => p.Key).Concat(ModScreens().Select(p => p.Key))
			.Distinct(StringComparer.Ordinal).OrderBy(k => uint.TryParse(k, out uint n) ? n : uint.MaxValue);
		foreach (string id in ids)
		{
			string name = _docs.Menus.Screens[id] is JsonObject screen ? Engine_UiMenuEdit.Text(screen, "name") : string.Empty;
			_screen.AddItem((ModScreens().ContainsKey(id) ? "● " : "    ") + id + (name.Length == 0 ? string.Empty : "  " + name));
			_screen.SetItemMetadata(_screen.ItemCount - 1, id);
			if (id == keep)
			{
				_screen.Select(_screen.ItemCount - 1);
			}
		}

		_filling = false;
		ShowScreen();
	}

	private string? ScreenId => _screen.Selected >= 0 ? (string)_screen.GetItemMetadata(_screen.Selected) : null;

	private void ShowScreen()
	{
		if (_filling)
		{
			return;
		}

		string? id = ScreenId;
		bool stated = id is not null && ModScreens().ContainsKey(id);
		JsonNode? record = id is null ? null : stated ? ModScreens()[id] : _docs.Menus.Screens[id];
		_filling = true;
		_screenText.Text = record?.ToJsonString(Engine_HudLayoutEdit.Indented) ?? string.Empty;
		_screenText.Editable = stated;
		_filling = false;
		_stateScreen.Disabled = id is null || stated;
		_dropScreen.Disabled = !stated;
		_screenNote.Text = id is null ? "No screen." : stated
			? $"Screen {id} as the mod states it — edited here, saved whole."
			: $"Screen {id} as it runs; read only until it is stated in the mod.";
	}

	private void StateScreen()
	{
		if (ScreenId is not { } id || _docs.Menus.Screens[id] is not JsonObject record)
		{
			return;
		}

		ModScreens()[id] = record.DeepClone();
		FillScreens();
		Edited?.Invoke();
	}

	private void DropScreen()
	{
		if (ScreenId is not { } id)
		{
			return;
		}

		ModScreens().Remove(id);
		FillScreens();
		Edited?.Invoke();
	}

	private void OnScreenText()
	{
		if (_filling || ScreenId is not { } id || !ModScreens().ContainsKey(id))
		{
			return;
		}

		try
		{
			if (JsonNode.Parse(_screenText.Text) is JsonObject record)
			{
				ModScreens()[id] = record;
				_screenNote.Text = $"Screen {id} as the mod states it — edited here, saved whole.";
				_screenNote.Modulate = new Color(0.72f, 0.75f, 0.80f);
				Edited?.Invoke();
				return;
			}

			_screenNote.Text = "A screen record is a JSON object.";
		}
		catch (JsonException exception)
		{
			_screenNote.Text = "Not JSON yet: " + exception.Message;
		}

		_screenNote.Modulate = ModStudioUiStyles.ErrorColour;
	}

	// ---------------------------------------------------------------------------------------
	// The tab's hooks
	// ---------------------------------------------------------------------------------------

	public void Activate()
	{
		_active = true;
		_preview.EnsureHost();
		_look = new OptionButton { TooltipText = "The art the list is drawn with: the front end's, or the books' (pause, and the screens' lists)" };
		_look.AddItem("Front end");
		_look.AddItem("Book");
		_look.ItemSelected += _ => BuildView();
		_preview.Toolbar.AddChild(_look);
		var back = new Button { Text = "◀ Back", TooltipText = "B: close the menu opened over the list" };
		back.Pressed += Back;
		_preview.Toolbar.AddChild(back);
		_trail = new Label();
		_preview.Toolbar.AddChild(_trail);
		_preview.Below.AddChild(ModStudioUiStyles.Note("Click a row to highlight it; click the highlighted row to press it. The wheel moves the highlight."));
		_preview.Pressed += OnPressed;
		_preview.Wheel += OnWheel;
		BuildView();
	}

	public void Deactivate()
	{
		_active = false;
		_preview.Pressed -= OnPressed;
		_preview.Wheel -= OnWheel;
		FreeView();
		foreach (Node child in _preview.Toolbar.GetChildren().Concat(_preview.Below.GetChildren()))
		{
			child.QueueFree();
		}

		_look = null;
		_trail = null;
	}

	/// <summary>The validator's issues for the templates file as it would be saved, and the menu and screen conflicts.</summary>
	public IEnumerable<(bool Error, string Text)> Issues(string templatesText, IReadOnlyList<Engine_UiTemplates.ModConflict> conflicts)
	{
		foreach (Refii.Mods.ModIssue issue in Refii.Mods.ModValidate.Validate(Engine_UiTemplates.ModTemplatesPath, templatesText))
		{
			yield return (issue.Error, issue.ToString());
		}

		foreach (Engine_UiTemplates.ModConflict conflict in conflicts.Where(c => c.Piece.StartsWith("menu:", StringComparison.Ordinal) ||
			c.Piece.StartsWith("screen:", StringComparison.Ordinal)))
		{
			if (UiStudioView.DescribeConflict(conflict, _docs.Mod) is { } text)
			{
				yield return (false, text);
			}
		}

		foreach ((string name, JsonObject record) in Editor.ChangedRecords())
		{
			foreach (string problem in RowProblems(Engine_UiMenuEdit.Entries(record)))
			{
				yield return (true, $"menu '{name}': {problem}");
			}
		}
	}

	private static IEnumerable<string> RowProblems(JsonArray entries)
	{
		var seen = new HashSet<string>(StringComparer.Ordinal);
		foreach (string problem in Walk(entries))
		{
			yield return problem;
		}

		IEnumerable<string> Walk(JsonArray list)
		{
			foreach (JsonObject row in list.OfType<JsonObject>())
			{
				string name = Engine_UiMenuEdit.Name(row);
				if (name.Length == 0)
				{
					yield return "a row has no name";
				}
				else if (!seen.Add(name))
				{
					yield return $"two rows are named '{name}'";
				}

				if (Engine_UiMenuEdit.Check(Engine_UiMenuEdit.Text(row, "command")) is { Length: > 0 } bad)
				{
					yield return $"'{name}': {bad}";
				}

				foreach (string nested in Walk(Engine_UiMenuEdit.Children(row)))
				{
					yield return nested;
				}
			}
		}
	}

	// ---------------------------------------------------------------------------------------
	// The preview — the game's own list view
	// ---------------------------------------------------------------------------------------

	private void FreeView()
	{
		if (_view is not null && GodotObject.IsInstanceValid(_view))
		{
			_view.QueueFree();
		}

		_view = null;
	}

	/// <summary>A menu record as the runtime model, through the template's own construction.</summary>
	private static Engine_FrontEndMenu? Model(string name, JsonObject record) =>
		Engine_UiTemplates.ParseMenuRecord(name, UiStudioDocs.ToGodot(record))?.Build();

	/// <summary>
	/// Builds the list view for the menu the editor shows, the way the game builds it: the row and
	/// the highlight cloned from the presentation's pattern book, anchored where the book puts its
	/// button pool.
	/// </summary>
	private void BuildView()
	{
		if (!_active || !_preview.Loaded)
		{
			return;
		}

		FreeView();
		_root = Editor.CurrentMenu;
		if (_root is null || Editor.Record(_root) is not { } record || Model(_root, record) is not { } menu)
		{
			_preview.SetStatus("No menu to show.");
			return;
		}

		UiStudioPreviewHost host = _preview.Host!;
		bool book = _look?.Selected == 1;
		string presentation = book ? PausePresentation : FrontEndPresentation;
		Engine_UiTheme? theme = host.PatternBookFor(presentation);
		_view = Engine_FrontEndMenuView.Dressed("StudioMenu", theme, book, host.DocumentFor(presentation), host.Theme);
		Node3D? template = _view.RowTemplate;
		host.AddChild(_view);
		_view.Bind(menu);
		Trail();
		_preview.SetStatus($"'{_root}' through the game's list view — {menu.Visible.Count} row(s), row art " +
			(template is null ? "not found (text-only rows)" : "cloned from " + presentation) + ".");
	}

	private void Trail()
	{
		if (_trail is not null && GodotObject.IsInstanceValid(_trail) && _view is not null)
		{
			_trail.Text = _view.OpenedMenuName is { } open ? $"{_root} › … › {open} ({_view.OpenedDepth} open)" : _root ?? string.Empty;
		}
	}

	private void OnWheel(int direction)
	{
		if (_view is not null && GodotObject.IsInstanceValid(_view))
		{
			_view.Navigate(direction, 0.0);
		}
	}

	private void Back()
	{
		if (_view is not null && GodotObject.IsInstanceValid(_view) && !_view.CloseMenu())
		{
			_preview.SetStatus("Back at the list the preview started from.");
		}

		Trail();
	}

	private void OnPressed(Vector2 pixel, MouseButton button)
	{
		if (_view is null || !GodotObject.IsInstanceValid(_view) || _preview.CanvasAt(pixel) is not { } canvas)
		{
			return;
		}

		if (button == MouseButton.Right)
		{
			Back();
			return;
		}

		if (button != MouseButton.Left || _view.EntryAt(canvas) is not { } item)
		{
			return;
		}

		if (_view.Menu?.Highlighted != item)
		{
			_view.PointAt(item);
			return;
		}

		Press(item);
	}

	/// <summary>
	/// A on the highlighted row, as the lists answer it: the view folds and unfolds; an
	/// <c>opens</c> naming a menu (on a row with no children) or an <c>Opens:</c> command opens that
	/// menu over the list; a <c>Mod:lua:</c> row and any other command are named, since they run in
	/// the game.
	/// </summary>
	private void Press(Engine_UiExpandableMenu.Item item)
	{
		string? dispatched = _view!.Activate();
		_view.TakeOpenedScreen();
		Engine_UiModMenuCommand.Parsed command = Engine_UiModMenuCommand.Parse(item.Command);
		string? opens = item.Opens is { Length: > 0 } named && item.Children.Count == 0 ? named :
			command.Verb == Engine_UiModMenuCommand.Verb.Opens ? command.Menu : null;

		if (opens is not null)
		{
			if (Editor.Record(opens) is { } record && Model(opens, record) is { } menu)
			{
				_view.OpenMenu(menu, opens);
				_preview.SetStatus($"'{item.Name}' opened '{opens}' ({menu.Visible.Count} row(s)); Back or right-click returns.");
			}
			else
			{
				_preview.SetStatus($"'{item.Name}' opens '{opens}', which is no menu the game or the mods state — in game it opens a screen of that name, or nothing.");
			}

			Trail();
			return;
		}

		_preview.SetStatus(command.Verb == Engine_UiModMenuCommand.Verb.Lua
			? $"'{item.Name}' calls {command.Function}({(command.Argument is null ? "nil" : "\"" + command.Argument + "\"")}, e) in the game's Lua — in game only."
			: item.Children.Count != 0 ? $"'{item.Name}' folds its {item.Children.Count} row(s)."
			: item.Command is { Length: > 0 } other ? $"'{item.Name}' runs '{other}' in the game."
			: dispatched is not null ? $"'{item.Name}' is dispatched to the game as '{dispatched}'."
			: $"'{item.Name}' does nothing here.");
	}
}
#endif
