namespace Refii.Titles.Fable3;

using System.Collections.Generic;
using System.Runtime.InteropServices;
using Godot;
using Refii.Host;
using Refii.Text;
using Refii.Ui;

/// <summary>
/// Fable 3's per-screen views over what the guest states: the interaction mode (the dialogue
/// prompts over a villager, shop or plinth) and the box-manager screens whose rows arrive as
/// element writes (<see cref="Fable3Elements"/>). The Options screen has its own view on the
/// shared menu template (<see cref="Fable3Options"/>).
/// </summary>
/// <remarks>
/// THE INTERACTION MODE is stated twice over. Its list comes from the Lua TABLE the script hands
/// `OpenInteractionMode` / `RefreshInteractionMode` (`ListOfOptions[i].TextTag/Icon/Message`,
/// `CurrentListIndex` — sub_8295B338 / sub_8295B5E8 read them; the hook flattens the nested rows
/// to `ListOfOptions.1.TextTag`) — the list's own widget is a Lua behaviour
/// (`InteractionMenuScrollableList.lua`) that writes no element through the C++ API; its BUTTONS
/// are element writes on interactionmode.bgf's `InteractionMenuRight`, drawn by the shared HUD view
/// with the guest's own glyph, label and enabled state (<see cref="Fable3Hud.InteractionGroups"/>); its text rows are
/// joined by the guest into ONE text box (sub_82958878 sets the joined string on the wrapper at
/// mode+384, whose commit is an `@Element.Text` on the `….TextBox.Text` element); the cursor is
/// the mode object's own `+116`, read live (RefiiHostGetFable3InteractionMode) because the guest
/// moves it on its own stick handling and posts message 137 with it. The guest answers the
/// prompt itself (A posts `A_Message` and the highlighted row's `Message`), so this draws only.
///
/// THE OTHER BOX-MANAGER SCREENS (hero status, stats, promises, quest summary, king's schedule;
/// display_box_hooks.cpp names each class `screen:…`) write their rows as elements under their
/// own root; <see cref="Fable3StatedScreen"/> draws them on the shared menu template.
/// </remarks>
public partial class Fable3Screens : Control
{
	private const float ReferenceHeight = 720f;
	private const float Padding = 16f;
	private const float PanelWidth = 420f;
	private const float RightMargin = 40f;
	private const float BottomMargin = 120f;

	private static readonly Color Panel = new(0f, 0f, 0f, 0.7f);
	private static readonly Color Highlight = new(1f, 1f, 1f, 0.15f);

	private UiFont? _font;
	private string _culture = string.Empty;
	private ulong _elements = ulong.MaxValue;
	private ulong _interaction = ulong.MaxValue;
	private int _highlight = -1;
	private bool _live;

	public override void _Ready()
	{
		SetAnchorsPreset(LayoutPreset.FullRect);
		MouseFilter = MouseFilterEnum.Ignore;
	}

	public override void _Process(double delta)
	{
		if (_culture != Engine_GameCulture.Current)
		{
			_culture = Engine_GameCulture.Current;
			Engine_UiFontLibrary fonts = Fable3Fonts.For(_culture);
			_font = fonts.Get(UiFontBucket.Normal);
			QueueRedraw();
		}

		(bool live, int highlight) = ReadInteractionMode();
		if (live != _live || highlight != _highlight || Fable3Elements.Generation != _elements ||
			Fable3Interaction.Generation != _interaction)
		{
			_live = live;
			_highlight = highlight;
			_elements = Fable3Elements.Generation;
			_interaction = Fable3Interaction.Generation;
			QueueRedraw();
		}
	}

	public override void _Draw()
	{
		if (_font is null)
		{
			return;
		}

		Vector2 size = GetViewportRect().Size;
		float scale = size.Y / ReferenceHeight;
		Engine_BabelDatabase? strings = Fable3Strings.Current;

		// The interaction prompt is a HUD surface: hidden with the HUD root (Fable3Elements.HudHidden).
		if (_live && Fable3Interaction.IsOpen && !Fable3Elements.HudHidden)
		{
			DrawInteraction(size, scale, strings);
		}

	}

	/// <summary>The prompt panel: the joined text rows, then the list with the guest's cursor.</summary>
	private void DrawInteraction(Vector2 size, float scale, Engine_BabelDatabase? strings)
	{
		float width = Mathf.Min(PanelWidth * scale, size.X - (2f * Padding * scale));
		float inner = width - (2f * Padding * scale);
		float lineHeight = _font!.Font.LineHeight * scale;
		float rowHeight = lineHeight + (6f * scale);

		string text = Fable3Elements.Find(path => path.EndsWith("TextBox.Text", System.StringComparison.Ordinal))?.Text ?? string.Empty;
		int textLines = text.Length == 0 ? 0 : Engine_UiCanvasText.MeasureWrappedLines(_font, text, inner, scale);

		IReadOnlyList<Fable3Interaction.Row> rows = Fable3Interaction.Rows;
		if (textLines == 0 && rows.Count == 0)
		{
			return;
		}

		float height = (textLines * lineHeight) + (rows.Count * rowHeight) + (3f * Padding * scale);
		var rect = new Rect2(new Vector2(size.X - (RightMargin * scale) - width, size.Y - (BottomMargin * scale) - height), new Vector2(width, height));
		DrawRect(rect, Panel);

		Vector2 pen = rect.Position + new Vector2(Padding * scale, Padding * scale);
		if (textLines > 0)
		{
			pen.Y += Engine_UiCanvasText.DrawWrapped(this, _font, text, pen, inner, scale, Colors.White) + (Padding * 0.5f * scale);
		}

		for (int i = 0; i < rows.Count; ++i)
		{
			if (i == _highlight)
			{
				DrawRect(new Rect2(pen - new Vector2(6f * scale, 2f * scale), new Vector2(inner + (12f * scale), rowHeight)), Highlight);
			}

			Engine_UiCanvasText.DrawLine(this, _font, Engine_UiGuiText.Resolve(rows[i].TextTag, strings), pen, scale, Colors.White);
			pen.Y += rowHeight;
		}
	}

	// ---- The interaction mode as its manager holds it (icon_feed.cpp) --------------------------

	private static readonly uint[] _modeWords = new uint[4];

	/// <summary>Whether a mode-3 interaction is open in the guest, and its highlighted list row.</summary>
	private static (bool Live, int Highlight) ReadInteractionMode()
	{
		if (!HostBridge.IsGuestUp)
		{
			return (false, -1);
		}

		try
		{
			if (RefiiHostGetFable3InteractionMode(_modeWords, (uint)_modeWords.Length) == 0u)
			{
				return (false, -1);
			}
		}
		catch (System.EntryPointNotFoundException)
		{
			return (false, -1);
		}

		bool live = _modeWords[0] != 0u && _modeWords[1] == 3u && _modeWords[3] != 0u;
		return (live, live ? (int)_modeWords[2] : -1);
	}

	[DllImport(HostBridge.Library)]
	private static extern uint RefiiHostGetFable3InteractionMode([Out] uint[] words, uint capacity);
}

/// <summary>
/// The interaction mode's stated table: the four button slots and the scrollable list, as the
/// `OpenInteractionMode` / `RefreshInteractionMode` natives hand them (keys per sub_8295B338 and
/// sub_8295B5E8), and the count of box-manager screens showing.
/// </summary>
public static class Fable3Interaction
{
	public readonly record struct Row(string TextTag, string Icon, string Message);

	private static readonly List<Row> _rows = new();

	public static IReadOnlyList<Row> Rows => _rows;

	public static bool IsOpen { get; private set; }

	/// <summary>`CurrentListIndex` as the table stated it; the live cursor is the mode object's.</summary>
	public static int StatedIndex { get; private set; }

	public static ulong Generation { get; private set; }

	/// <summary>Box-manager screens showing now (every `screen:…` class and the unnamed ones).</summary>
	public static int ScreenBoxes { get; private set; }

	/// <summary>
	/// The element root of the front screen whose rows are element writes, or null: the hooks'
	/// `screen:…` kind names the class, and each class writes under its own root (the options
	/// screen has its own view and names none here).
	/// </summary>
	public static string? ScreenRoot { get; private set; }

	/// <summary>
	/// The element root a screen class writes under — the first segment of every path its Show
	/// writes (sub_829758C8 `HeroStatus.Section1…`, sub_827A3398 `Promises.…`, sub_8296D4A0
	/// `StatsBox`, sub_82973AC8 `QuestSummary.ObjectiveTitle`, sub_82972FD8 `KingsSchedule.Title`).
	/// </summary>
	public static string? RootOf(string kind) => kind switch
	{
		"screen:herostatus" => "HeroStatus",
		"screen:promises" => "Promises",
		"screen:stats" => "StatsBox",
		"screen:quests" => "QuestSummary",
		"screen:schedule" => "KingsSchedule",
		_ => null,
	};

	public static void Opened(Engine_UiModel.GuiNativeArgument table)
	{
		IsOpen = true;
		Refreshed(table);
	}

	public static void Refreshed(Engine_UiModel.GuiNativeArgument table)
	{
		_rows.Clear();
		for (int i = 1; i <= 64; ++i)
		{
			string prefix = "ListOfOptions." + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".";
			if (table.Fields is null || !table.Fields.ContainsKey(prefix + "TextTag"))
			{
				break;
			}

			_rows.Add(new Row(table.FieldText(prefix + "TextTag", string.Empty), table.FieldText(prefix + "Icon", string.Empty),
				table.FieldText(prefix + "Message", string.Empty)));
		}

		StatedIndex = (int)table.FieldNumber("CurrentListIndex", 0f);
		++Generation;
	}

	public static void Closed()
	{
		IsOpen = false;
		_rows.Clear();
		++Generation;
	}

	public static void ScreenBoxShown(int delta, string? root = null)
	{
		ScreenBoxes = System.Math.Max(0, ScreenBoxes + delta);
		if (delta > 0)
		{
			ScreenRoot = root;
		}
		else if (ScreenBoxes == 0 || root == ScreenRoot)
		{
			ScreenRoot = null;
		}

		++Generation;
	}
}
