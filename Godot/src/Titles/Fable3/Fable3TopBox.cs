namespace Refii.Titles.Fable3;

using System.Collections.Generic;
using System.Runtime.InteropServices;
using Godot;
using Refii.Text;
using Refii.Ui;

/// <summary>
/// Fable 3's TopBox — quest notices, objectives, info boxes, map-table text — drawn from what the
/// guest states: whether its manager is showing a box (RefiiHostGetFable3TopBox,
/// Fable3/src/hostbridge/src/hud_feed.cpp) and the strings its publisher wrote into the HUD text
/// registry (`TopTitle`, `TopMain`, `RightTopButton`, `LeftTopButton`).
/// </summary>
/// <remarks>
/// WHICH STRINGS ARE CURRENT is the publisher's rule (sub_8217D880), applied to the front record
/// the bridge reads: the title shows only when the book has a string for the record's title key
/// (its `sub_8230DA98(textDB, rec+4)`), and a box with neither title nor body is button-only and
/// rewrites neither text — so those registry strings belong to an older box and are not drawn.
/// The buttons are the publisher's count 1 + A + Y: one button shows A, or Y when A is off; two
/// show A on the right and Y on the left, each with its own label. A hold button's fill is the
/// `RightTopButton` / `LeftTopButton` meter the guest steps (0..100).
///
/// The box's place and look are ours: top centre, laid out in a 720-high reference and scaled
/// with the window so the text stays legible at any size.
/// </remarks>
public partial class Fable3TopBox : Control
{
	private const float ReferenceHeight = 720f;
	private const float BoxWidth = 560f;
	private const float Top = 36f;
	private const float Padding = 14f;
	private const float ButtonGap = 18f;

	private static readonly uint TopTitle = Refii.Core.Engine_GameHash.Fnv1("TopTitle");
	private static readonly uint TopMain = Refii.Core.Engine_GameHash.Fnv1("TopMain");
	private static readonly uint RightButton = Refii.Core.Engine_GameHash.Fnv1("RightTopButton");
	private static readonly uint LeftButton = Refii.Core.Engine_GameHash.Fnv1("LeftTopButton");

	private readonly uint[] _words = new uint[8];
	private UiFont? _font;
	private string _culture = string.Empty;

	private bool _up;
	private string _title = string.Empty;
	private string _body = string.Empty;
	private readonly List<(string Letter, string Label, float Fill)> _buttons = new();
	private string _stated = string.Empty;

	public override void _Ready()
	{
		SetAnchorsPreset(LayoutPreset.FullRect);
		MouseFilter = MouseFilterEnum.Ignore;
	}

	public override void _Process(double delta)
	{
		if (_font is null || _culture != Engine_GameCulture.Current)
		{
			_culture = Engine_GameCulture.Current;
			_font = Engine_UiFontLibrary.Load(Refii.Core.Engine_GameData.Resolve(), _culture).Get(UiFontBucket.Normal);
		}

		bool up = Read(out uint titleKey, out uint bodyKey, out bool showA, out bool holdA, out bool showY, out bool holdY);
		string title = string.Empty;
		string body = string.Empty;
		_buttons.Clear();
		if (up)
		{
			Engine_BabelDatabase? strings = Fable3Strings.Current;
			bool hasTitle = Has(strings, titleKey);
			bool buttonOnly = !hasTitle && !Has(strings, bodyKey);
			Dictionary<uint, string> texts = Engine_UiSource.Current.GetHudTexts();
			Dictionary<uint, float> meters = Engine_UiSource.Current.GetHudMeters();
			if (!buttonOnly)
			{
				title = hasTitle && texts.TryGetValue(TopTitle, out string? t) ? t : string.Empty;
				body = texts.TryGetValue(TopMain, out string? b) ? b : string.Empty;
			}

			if (showA || showY)
			{
				_buttons.Add((showA ? "A" : "Y", Text(texts, RightButton), (showA ? holdA : holdY) ? Meter(meters, RightButton) : -1f));
			}

			if (showA && showY)
			{
				_buttons.Add(("Y", Text(texts, LeftButton), holdY ? Meter(meters, LeftButton) : -1f));
			}
		}

		string stated = up ? $"{title}|{body}|{_buttons.Count}" : string.Empty;
		if (stated != _stated)
		{
			_stated = stated;
			GD.Print(up
				? $"[fable3-topbox] up: title '{title}', body '{body}', {_buttons.Count} button(s)"
				: "[fable3-topbox] down");
		}

		_up = up;
		_title = title;
		_body = body;
		QueueRedraw();
	}

	private bool Read(out uint titleKey, out uint bodyKey, out bool showA, out bool holdA, out bool showY, out bool holdY)
	{
		titleKey = bodyKey = 0u;
		showA = holdA = showY = holdY = false;
		uint count;
		try
		{
			count = RefiiHostGetFable3TopBox(_words, (uint)_words.Length);
		}
		catch (System.EntryPointNotFoundException)
		{
			return false;
		}
		catch (System.DllNotFoundException)
		{
			return false;
		}

		if (count < 8u || _words[0] == 0u)
		{
			return false;
		}

		titleKey = _words[1];
		bodyKey = _words[2];
		showA = _words[3] != 0u;
		holdA = _words[4] != 0u;
		showY = _words[5] != 0u;
		holdY = _words[6] != 0u;
		return true;
	}

	/// <summary>`sub_8230DCA8` yields no string for an absent key; an empty one draws nothing either.</summary>
	private static bool Has(Engine_BabelDatabase? strings, uint key) =>
		strings is not null && strings.TryGetString(key, out string text) && text.Length > 0;

	private static string Text(Dictionary<uint, string> texts, uint hash) =>
		texts.TryGetValue(hash, out string? text) ? text : string.Empty;

	private static float Meter(Dictionary<uint, float> meters, uint hash) =>
		meters.TryGetValue(hash, out float value) ? Mathf.Clamp(value / 100f, 0f, 1f) : 0f;

	public override void _Draw()
	{
		if (!_up || _font is null || (_title.Length == 0 && _body.Length == 0 && _buttons.Count == 0))
		{
			return;
		}

		Vector2 size = GetViewportRect().Size;
		float scale = size.Y / ReferenceHeight;
		float width = System.Math.Min(BoxWidth * scale, size.X - (2f * Padding * scale));
		float inner = width - (2f * Padding * scale);
		float lineHeight = _font.Font.LineHeight * scale;
		int titleLines = _title.Length == 0 ? 0 : Engine_UiCanvasText.MeasureWrappedLines(_font, _title, inner, scale);
		int bodyLines = _body.Length == 0 ? 0 : Engine_UiCanvasText.MeasureWrappedLines(_font, _body, inner, scale);
		float buttons = _buttons.Count == 0 ? 0f : lineHeight + (titleLines + bodyLines > 0 ? Padding * 0.5f * scale : 0f);
		float height = ((titleLines + bodyLines) * lineHeight) + buttons + (2f * Padding * scale);
		var box = new Rect2(new Vector2((size.X - width) * 0.5f, Top * scale), new Vector2(width, height));
		DrawRect(box, new Color(0f, 0f, 0f, 0.6f));

		Vector2 pen = box.Position + new Vector2(Padding * scale, Padding * scale);
		if (titleLines > 0)
		{
			pen.Y += Engine_UiCanvasText.DrawWrapped(this, _font, _title, pen, inner, scale, new Color(1f, 0.86f, 0.55f));
		}

		if (bodyLines > 0)
		{
			pen.Y += Engine_UiCanvasText.DrawWrapped(this, _font, _body, pen, inner, scale, Colors.White);
		}

		if (_buttons.Count == 0)
		{
			return;
		}

		// Right-aligned, the first button rightmost — the publisher's Right/Left order.
		float x = box.End.X - (Padding * scale);
		float y = box.End.Y - (Padding * scale) - lineHeight;
		foreach ((string letter, string label, float fill) in _buttons)
		{
			string line = $"{letter}  {label}";
			float lineWidth = Engine_UiCanvasText.Measure(_font, line) * scale;
			x -= lineWidth;
			if (fill >= 0f)
			{
				DrawRect(new Rect2(new Vector2(x, y + lineHeight - (2f * scale)), new Vector2(lineWidth * fill, 2f * scale)), Colors.White);
			}

			Engine_UiCanvasText.DrawLine(this, _font, line, new Vector2(x, y), scale, Colors.White);
			x -= ButtonGap * scale;
		}
	}

	[DllImport(Refii.Host.HostBridge.Library)]
	private static extern uint RefiiHostGetFable3TopBox([Out] uint[] words, uint capacity);
}
