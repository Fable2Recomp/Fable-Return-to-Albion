namespace Refii.Titles.Fable3;

using System.Collections.Generic;
using Godot;
using Refii.Text;
using Refii.Ui;

/// <summary>
/// One page of a Fable 3 box-manager screen as Godot draws it: its title, its rows (each one line,
/// label then value), the guest's cursor row (or none), the cursor row's description and the
/// button prompts. Pure data, so the screens' models can be tested.
/// </summary>
public readonly record struct Fable3ScreenPage(
	string Title, IReadOnlyList<Fable3ScreenPage.Row> Rows, int Selected, string Description,
	IReadOnlyList<Fable3ScreenPage.Prompt> Prompts, bool Cursor, Fable3ScreenPage.QueryBox? Query = null)
{
	/// <summary>
	/// A question the screen asks over its page (Options' New Game / overwrite confirmations): an
	/// optional title, the question and its prompts. The guest answers it from the pad.
	/// </summary>
	public sealed record QueryBox(string Title, string Text, IReadOnlyList<Prompt> Prompts);

	/// <summary>One row: a stable name, the shown line, ghosting, and its description (a tag or text).</summary>
	public readonly record struct Row(string Name, string Line, bool Ghosted = false, string Description = "");

	/// <summary>A pad button and its label.</summary>
	public readonly record struct Prompt(string Button, string Label);

	public static readonly Fable3ScreenPage Closed = new(string.Empty, System.Array.Empty<Row>(), -1, string.Empty, System.Array.Empty<Prompt>(), false);
}

/// <summary>
/// THE ONE MENU TEMPLATE for Fable 3's box-manager screens: the list is the SHARED
/// <see cref="Engine_FrontEndMenuView"/> (layout, padding, carousel and cursor all live there),
/// its rows the page's lines and its highlight the guest's cursor row; the title, the cursor row's
/// description and the button prompts are drawn over it at the canvas's 720-unit scale, so the
/// screen adapts to any window as every other shared surface does. A screen supplies its page
/// (<see cref="Compose"/>); the pad reaches the guest unchanged, so a screen draws and never decides.
/// </summary>
public abstract partial class Fable3ListScreen : Fable3Canvas
{
	private const float TitleY = 40f;
	private const float DescriptionBottom = 110f;
	private const float PromptsBottom = 56f;
	private const float PromptGap = 36f;
	private const float Padding = 16f;

	private static readonly Color TitleColour = new(1f, 0.86f, 0.55f);
	private static readonly Color Panel = new(0f, 0f, 0f, 0.55f);

	// The query box as options.bgf lays it out (canvas units about the screen centre, y up):
	// `QueryBox.Group.QueryBox.Title` y +88, gold 241/201/115 size 18; `.Text` y +3, box 0.55 of
	// the width, size 16; `.Buttons` at (+80, −79) with `A_Button` x −400 (label right of the
	// orb, +20) and `B_Button` x +240 (label left of the orb, −20).
	private const float QueryTitleY = 88f;
	private const float QueryTextY = 3f;
	private const float QueryTextWidth = 0.55f * 1280f;
	private const float QueryButtonsY = -79f;
	private const float QueryLeftButtonX = 80f - 400f;
	private const float QueryRightButtonX = 80f + 240f;
	private static readonly Color QueryTitleColour = new(241f / 255f, 201f / 255f, 115f / 255f);
	private static readonly Color QueryDim = new(0f, 0f, 0f, 0.6f);

	private Engine_FrontEndMenuView _view = null!;
	private readonly Engine_FrontEndMenu _menu = new();
	private Fable3ScreenPage _page = Fable3ScreenPage.Closed;
	private string _shown = string.Empty;
	private string _description = string.Empty;
	private int _selected = -1;
	private bool _open;
	private Fable3ScreenPage.QueryBox? _query;
	private string _querySignature = string.Empty;
	private bool _probed;

	/// <summary>The log tag of the screen, `[tag] …`.</summary>
	protected abstract string LogTag { get; }

	/// <summary>What the log line names the page by, besides its title (Options: its page number).</summary>
	protected virtual string LogContext => string.Empty;

	/// <summary>The page this frame, or null when the screen is not up in the guest.</summary>
	protected abstract Fable3ScreenPage? Compose(double delta);

	/// <summary>Whether the screen is up in the guest this frame.</summary>
	public bool IsOpen => _open;

	/// <summary>A tag through the title's strings; text that is not a tag is returned as is.</summary>
	protected static string Resolve(string tag) => Engine_UiGuiText.Resolve(tag, Fable3Strings.Current);

	protected override void Build()
	{
		_view = Engine_FrontEndMenuView.Dressed(Name + "List", null, false, null, Theme);
		_view.GuestCursor = true;
		_view.Visible = false;
		_view.Bind(_menu);
		Root.AddChild(_view);

		// A closed screen neither shows nor renders its canvas: the container hides and the
		// viewport is switched off with it (Frame). NOT `WhenParentVisible` — that mode means the
		// parent VIEWPORT (the window) and the renderer draws children before parents, so the
		// canvas never rendered at all: r6/r7 logged `view drew 3` with no row on screen while the
		// title and description, drawn on this container, stood.
		CanvasViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
		Visible = false;
	}

	/// <summary>
	/// The view's rows are glyph meshes built from the theme's fonts when the rows are built, so
	/// a theme arriving after `_Ready` (the strings load on a later frame) must REDRESS the view —
	/// setting `Theme` alone left every label without glyphs (r4).
	/// </summary>
	protected override void ThemeChanged()
	{
		_view.Redress(Theme, null, null, null);
		_shown = string.Empty;
		QueueRedraw();
	}

	protected override void Frame(double delta)
	{
		Fable3ScreenPage? composed = Compose(delta);
		bool open = composed is not null;
		if (open != _open)
		{
			_open = open;
			// Clicks made before the screen stood are not picks on it.
			Engine_UiPointer.EndFrame();
			Fable3PointerSteps.Cancel();
			Visible = open;
			_view.Visible = open;
			CanvasViewport.RenderTargetUpdateMode = open ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
			_shown = string.Empty;
			QueueRedraw();
			if (!open)
			{
				_menu.Clear();
				_view.Refresh();
				_page = Fable3ScreenPage.Closed;
				_query = null;
				_querySignature = string.Empty;
				_probed = false;
			}
		}

		if (composed is not { } page)
		{
			return;
		}

		// The list is rebuilt only when a line changed; the highlight follows every frame.
		var signature = new System.Text.StringBuilder();
		signature.Append(page.Title).Append('|');
		foreach (Fable3ScreenPage.Row row in page.Rows)
		{
			signature.Append(row.Name).Append('=').Append(row.Line).Append(row.Ghosted ? "-|" : "|");
		}

		foreach (Fable3ScreenPage.Prompt prompt in page.Prompts)
		{
			signature.Append(prompt.Button).Append(prompt.Label).Append('|');
		}

		bool rebuilt = false;
		if (signature.ToString() != _shown)
		{
			_shown = signature.ToString();
			_menu.Clear();
			foreach (Fable3ScreenPage.Row row in page.Rows)
			{
				_menu.Add(new Engine_UiExpandableMenu.Item(row.Name, row.Name) { StatedLabel = row.Line, Ghosted = row.Ghosted });
			}

			_selected = -1;
			rebuilt = true;
			QueueRedraw();
		}

		_page = page;
		_view.CursorShown = page.Cursor;
		Point(page, delta);

		// A query stands over the page: the list steps back while the guest waits for the answer.
		bool listShown = page.Query is null;
		if (_view.Visible != listShown)
		{
			_view.Visible = listShown;
			QueueRedraw();
		}

		// Compared by content: the page is composed afresh every frame.
		string querySignature = page.Query is { } query
			? $"'{query.Title}' '{query.Text}' [{string.Join(" | ", query.Prompts)}]"
			: string.Empty;
		if (querySignature != _querySignature)
		{
			_querySignature = querySignature;
			_query = page.Query;
			QueueRedraw();
			GD.Print(querySignature.Length > 0 ? $"[{LogTag}] query up: {querySignature}" : $"[{LogTag}] query down");
		}

		// The view lays its rows out only in Refresh — the model has no change event, so a list
		// repopulated or re-highlighted under a bound view stays undrawn until the owner says so
		// (the F2 pause list does the same after SetAvailable). r6 bound 3 Options rows and drew 0.
		int selected = page.Selected >= 0 && page.Selected < page.Rows.Count ? page.Selected : -1;
		if (selected != _selected || rebuilt)
		{
			_selected = selected;
			if (selected >= 0)
			{
				_menu.Highlight(page.Rows[selected].Name);
			}

			_view.Refresh();
			string description = selected >= 0 ? page.Rows[selected].Description : page.Description;
			description = description.Length == 0 ? page.Description : description;
			description = description.StartsWith("GUI_", System.StringComparison.Ordinal) ||
				description.StartsWith("TEXT_", System.StringComparison.Ordinal) ? Resolve(description) : description;
			if (description != _description)
			{
				_description = description;
				QueueRedraw();
			}
		}

		if (rebuilt)
		{
			var lines = new List<string>(page.Rows.Count);
			foreach (Fable3ScreenPage.Row row in page.Rows)
			{
				lines.Add(row.Line);
			}

			GD.Print($"[{LogTag}] {LogContext}'{page.Title}' row {page.Selected + 1}: {page.Rows.Count} row(s) bound, view drew {_view.RowsDrawn} " +
				$"(labels without glyphs {_view.LabelsWithoutGlyphs}, theme {(Theme?.Fonts is null ? "MISSING" : "bound")}, " +
				$"view {(_view.IsVisibleInTree() ? "shown" : "HIDDEN")}): {string.Join(" | ", lines)}");

			// "drew" counts rows made visible, not pixels: r7 and r8 logged `view drew 3` with no
			// row on screen. The probe reads the canvas back once per opening.
			if (!_probed)
			{
				_probed = true;
				Fable3CanvasProbe.Report(LogTag, this, CanvasViewport, CanvasCamera, _view);
			}
		}
	}

	/// <summary>
	/// THE MOUSE ON THE LIST: the pointer resolved through this canvas's camera (the open screen's),
	/// a hovered row picked when the pointer moves, a click picking and accepting it, the wheel stepping
	/// the cursor. Where the guest owns the cursor the pick is walked (<see cref="PointAt"/>).
	/// </summary>
	private void Point(Fable3ScreenPage page, double delta)
	{
		Engine_UiPointer.Resolve(CanvasCamera, CanvasViewport, GetViewportRect().Size);
		if (page.Query is null && _view.Visible && Engine_UiPointer.Canvas is { } at)
		{
			int row = -1;
			if (_view.EntryAt(at) is { } item)
			{
				for (int i = 0; i < page.Rows.Count; ++i)
				{
					if (page.Rows[i].Name == item.Name)
					{
						row = i;
						break;
					}
				}
			}

			bool clicked = Engine_UiPointer.TakeLeft();
			if (row >= 0 && (Engine_UiPointer.Moved || clicked))
			{
				PointAt(row, clicked);
			}

			int wheel = Engine_UiPointer.TakeWheel();
			if (wheel != 0 && page.Rows.Count > 0)
			{
				int from = page.Selected >= 0 ? page.Selected : 0;
				PointAt(System.Math.Clamp(from + wheel, 0, page.Rows.Count - 1), false);
			}
		}

		Fable3PointerSteps.Tick(page.Selected, delta);
	}

	/// <summary>The pointer picked <paramref name="row"/>; <paramref name="clicked"/> when it was a click. The guest's screens walk their own cursor to it.</summary>
	protected virtual void PointAt(int row, bool clicked) => Fable3PointerSteps.Toward(row, clicked);

	public override void _Draw()
	{
		if (!_open || Theme?.Fonts is null)
		{
			return;
		}

		UiFont font = Theme.Fonts.Get(UiFontBucket.Normal);
		Vector2 size = GetViewportRect().Size;
		float scale = size.Y / CanvasHeight;

		if (_page.Title.Length > 0)
		{
			float width = Engine_UiCanvasText.Measure(font, _page.Title) * scale * 1.25f;
			Engine_UiCanvasText.DrawLine(this, font, _page.Title, new Vector2((size.X - width) * 0.5f, TitleY * scale), scale * 1.25f, TitleColour);
		}

		if (_query is { } query)
		{
			DrawQuery(query, font, size, scale);
			return;
		}

		if (_description.Length > 0)
		{
			float inner = Mathf.Min(720f * scale, size.X - (2f * Padding * scale));
			int lines = Engine_UiCanvasText.MeasureWrappedLines(font, _description, inner, scale);
			float height = (lines * font.Font.LineHeight * scale) + (2f * Padding * scale);
			var rect = new Rect2(new Vector2((size.X - inner) * 0.5f - (Padding * scale), size.Y - (DescriptionBottom * scale) - height), new Vector2(inner + (2f * Padding * scale), height));
			DrawRect(rect, Panel);
			Engine_UiCanvasText.DrawWrapped(this, font, _description, rect.Position + new Vector2(Padding * scale, Padding * scale), inner, scale, Colors.White);
		}

		if (_page.Prompts.Count > 0)
		{
			float total = 0f;
			foreach (Fable3ScreenPage.Prompt prompt in _page.Prompts)
			{
				total += Fable3Prompts.Measure(font, prompt.Label, scale) + (PromptGap * scale);
			}

			var pen = new Vector2((size.X - total + (PromptGap * scale)) * 0.5f, size.Y - (PromptsBottom * scale));
			foreach (Fable3ScreenPage.Prompt prompt in _page.Prompts)
			{
				pen.X += Fable3Prompts.Draw(this, font, prompt.Button, prompt.Label, pen, scale, Colors.White) + (PromptGap * scale);
			}
		}
	}

	/// <summary>The query over a dimmed page, placed where options.bgf authors its parts.</summary>
	private void DrawQuery(Fable3ScreenPage.QueryBox query, UiFont font, Vector2 size, float scale)
	{
		DrawRect(new Rect2(Vector2.Zero, size), QueryDim);
		Vector2 centre = size * 0.5f;
		float lineHeight = font.Font.LineHeight * scale;

		if (query.Title.Length > 0)
		{
			float titleScale = scale * (18f / 16f);
			float width = Engine_UiCanvasText.Measure(font, query.Title) * titleScale;
			Engine_UiCanvasText.DrawLine(this, font, query.Title,
				new Vector2(centre.X - (width * 0.5f), centre.Y - (QueryTitleY * scale) - (lineHeight * 0.5f)), titleScale, QueryTitleColour);
		}

		float inner = Mathf.Min(QueryTextWidth * scale, size.X - (2f * Padding * scale));
		int lines = Engine_UiCanvasText.MeasureWrappedLines(font, query.Text, inner, scale);
		float height = lines * lineHeight;
		var panel = new Rect2(
			new Vector2(centre.X - (inner * 0.5f) - (Padding * scale), centre.Y - (QueryTextY * scale) - (height * 0.5f) - (Padding * scale)),
			new Vector2(inner + (2f * Padding * scale), height + (2f * Padding * scale)));
		DrawRect(panel, Panel);
		Engine_UiCanvasText.DrawWrapped(this, font, query.Text, panel.Position + new Vector2(Padding * scale, Padding * scale), inner, scale, Colors.White);

		// The first prompt stands on the left button's place, the second on the right's (its
		// label ends at the orb, as the B button's `-20` label offset authors it).
		float y = centre.Y - (QueryButtonsY * scale) - (lineHeight * 0.5f);
		for (int i = 0; i < query.Prompts.Count && i < 2; ++i)
		{
			Fable3ScreenPage.Prompt prompt = query.Prompts[i];
			float x = centre.X + ((i == 0 ? QueryLeftButtonX : QueryRightButtonX) * scale);
			if (i == 1)
			{
				x -= Fable3Prompts.Measure(font, prompt.Label, scale);
			}

			Fable3Prompts.Draw(this, font, prompt.Button, prompt.Label, new Vector2(x, y), scale, Colors.White);
		}
	}
}
