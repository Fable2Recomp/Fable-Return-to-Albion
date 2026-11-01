namespace Refii.Titles.Fable3;

using System;
using Godot;
using Refii.Text;
using Refii.Ui;

/// <summary>
/// Fable 3's "Press START" splash, drawn here from the guest's own constants and its stated state.
///
/// THE GUEST'S SCREEN (the splash F, vtable 0x82080AE0, Fable 3 IDB). Its art, rects, strings and
/// timings are constants in the binary, all in a 1280x720 space:
///   background  splash_screen.tex, two quads: (0,0)-(1024,720) from the texture's top 1024x720
///               (v 0..0.703125, 0x8235A450) and (1024,0)-(1280,720) from a strip stored ROTATED
///               in rows 0.75..1.0 (0x8235A574..0x8235A5C8)
///   logo        fable_iii_logoshadow.tex then fable_iii_logo.tex, (128,160)-(1152,416), or
///               (128,192)-(1152,384) when not widescreen (0x832A0E44 / 0x832A0E54)
///   prompt      the START icon (buttons/start/24x24/start_complete.tex) at (tx-iw,512)-(tx,544)
///               and TEXT_GUI_SPLASH_SCREEN_START in GUI_Normal at (tx, (32-lineH)/2+512), with
///               iw = 32 (42.67 when not widescreen) and tx = ((width - textW) - iw) / 2
///               (sub_8235AD40)
///   legal       TEXT_GUI_SPLASH_SCREEN_LEGAL in GUI_Small, wrapped in a 1024-wide box at
///               (640,592) (sub_8235ABF0)
/// Back to front by the depths the guest gives them: 3.4, 3.3, 3.2, 3.1, 3.0.
///
/// THE STATE (sub_82359848, stated by Fable3/src/hooks/src/splash_screen.cpp): 1 fades the icon
/// and the text in over 0.5 s; 2 pulses the icon at 0.6 + 0.4 cos(2 pi t) with a 1 s period, the
/// text at full; 3 holds the icon where it was; the screen stays until the guest's teardown.
/// The background and the logo never fade.
///
/// Not drawn yet: the seven fxgui_smoke_bullethole effects the guest lays over the screen.
/// </summary>
public partial class Fable3Splash : Control
{
	/// <summary>The host-only bus kind Fable 3's native screens are stated on.</summary>
	public const int StateKind = 1007;

	private const float Width = 1280f;
	private const float Height = 720f;
	private const string Frontend = "art/gui/hudassests/version4/middleelements/frontend/";
	private const string StartIcon = "art/gui/hudassests/version4/buttons/start/24x24/start_complete.tex";

	private int _state = -1;
	private bool _closed = true;
	private bool _wideScreen = true;
	private float _backBufferWidth = Width;
	private double _stateAt;
	private float _heldIconAlpha = 1f;

	private bool _loaded;
	private Texture2D? _background;
	private Texture2D? _logo;
	private Texture2D? _logoShadow;
	private Texture2D? _icon;
	private UiFont? _normal;
	private UiFont? _small;
	private string _start = string.Empty;
	private string _legal = string.Empty;

	public override void _Ready()
	{
		SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		MouseFilter = MouseFilterEnum.Ignore;
		Visible = false;
	}

	/// <summary>One line from the splash hook: <c>screen=splash,state=N,closed=0|1,...</c>.</summary>
	public void Stated(string fields)
	{
		int state = _state;
		bool closed = _closed;
		foreach ((string key, object value) in Refii.Mods.RefiiHost.ParseFieldsPlain(fields))
		{
			long number = value is long l ? l : 0;
			switch (key)
			{
				case "state":
					state = (int)number;
					break;
				case "closed":
					closed = number != 0;
					break;
				case "widescreen":
					_wideScreen = number != 0;
					break;
				case "width":
					_backBufferWidth = number > 0 ? number : Width;
					break;
			}
		}

		if (state != _state)
		{
			if (state == 3)
			{
				_heldIconAlpha = IconAlpha(Now());
			}

			_state = state;
			_stateAt = Now();
		}

		_closed = closed;
		bool shown = !_closed && _state >= 1;
		if (shown && !_loaded)
		{
			Load();
		}

		Visible = shown;
		QueueRedraw();
		GD.Print($"[fable3-splash] state {_state} closed {_closed} shown {shown}: size {Size}, in tree {IsInsideTree()}, " +
			$"background {_background is not null}, fonts {_normal is not null}/{_small is not null}, start '{_start}'");
	}

	private bool _drawnOnce;

	public override void _Process(double delta)
	{
		if (Visible)
		{
			QueueRedraw();
		}
	}

	private static double Now() => Time.GetTicksMsec() / 1000.0;

	private void Load()
	{
		_loaded = true;
		_background = Texture(Frontend + "splash_screen.tex");
		_logo = Texture(Frontend + "fable_iii_logo.tex");
		_logoShadow = Texture(Frontend + "fable_iii_logoshadow.tex");
		_icon = Texture(StartIcon);

		Engine_UiFontLibrary fonts = Fable3Fonts.For(Engine_GameCulture.Current);
		_normal = fonts.Get(UiFontBucket.Normal);
		_small = fonts.Get(UiFontBucket.Small);

		Engine_BabelDatabase? strings = Fable3Strings.Current;
		_start = Text(strings, "TEXT_GUI_SPLASH_SCREEN_START");
		_legal = Text(strings, "TEXT_GUI_SPLASH_SCREEN_LEGAL");
	}

	private static Texture2D? Texture(string path)
	{
		Texture2D? texture = Fable3Art.Load(path);
		if (texture is null)
		{
			GD.PushWarning($"[fable3-splash] not in the game's banks or did not decode: {path}");
		}

		return texture;
	}

	private static string Text(Engine_BabelDatabase? strings, string tag)
	{
		if (strings is not null && strings.TryGetString(tag, out string value))
		{
			return value;
		}

		GD.PushWarning($"[fable3-splash] no string {tag}");
		return string.Empty;
	}

	private float IconAlpha(double now)
	{
		double elapsed = now - _stateAt;
		return _state switch
		{
			1 => (float)Math.Min(1.0, elapsed * 2.0),
			2 => 0.6f + 0.4f * (float)Math.Cos(Math.Tau * (elapsed % 1.0)),
			>= 3 => _heldIconAlpha,
			_ => 0f,
		};
	}

	private float TextAlpha(double now) =>
		_state == 1 ? (float)Math.Min(1.0, (now - _stateAt) * 2.0) : 1f;

	public override void _Draw()
	{
		if (!_drawnOnce)
		{
			_drawnOnce = true;
			GD.Print($"[fable3-splash] first draw at {Size}, global {GetGlobalRect()}, layer visible {(GetParent() as CanvasLayer)?.Visible}");
		}

		// The window, not the control's own size: under a CanvasLayer the control measured 0x0 with
		// its anchors alone (2026-10-30), and this screen covers the window whatever its parent says.
		Vector2 size = GetViewportRect().Size;
		float scale = Math.Min(size.X / Width, size.Y / Height);
		Vector2 offset = (size - new Vector2(Width, Height) * scale) * 0.5f;
		DrawRect(new Rect2(Vector2.Zero, size), Colors.Black);

		Vector2 At(float x, float y) => offset + new Vector2(x, y) * scale;

		if (_background is not null)
		{
			Quad(_background, At(0, 0), At(1024, 0), At(1024, 720), At(0, 720),
				new(0, 0), new(1, 0), new(1, 0.703125f), new(0, 0.703125f), Colors.White);
			Quad(_background, At(1024, 0), At(1280, 0), At(1280, 720), At(1024, 720),
				new(0.703125f, 0.75f), new(0.703125f, 1f), new(0, 1f), new(0, 0.75f), Colors.White);
		}

		(float top, float bottom) = _wideScreen ? (160f, 416f) : (192f, 384f);
		foreach (Texture2D? logo in new[] { _logoShadow, _logo })
		{
			if (logo is not null)
			{
				DrawTextureRect(logo, new Rect2(At(128, top), new Vector2(1024, bottom - top) * scale), false);
			}
		}

		double now = Now();
		float iconWidth = _wideScreen ? 32f : 42.666668f;
		if (_normal is not null && _start.Length > 0)
		{
			float textWidth = Engine_UiCanvasText.Measure(_normal, _start);
			float tx = (_backBufferWidth - textWidth - iconWidth) * 0.5f;
			if (_icon is not null)
			{
				DrawTextureRect(_icon, new Rect2(At(tx - iconWidth, 512), new Vector2(iconWidth, 32) * scale), false,
					new Color(1, 1, 1, IconAlpha(now)));
			}

			float y = (32f - _normal.Font.LineHeight) * 0.5f + 512f;
			Engine_UiCanvasText.DrawLine(this, _normal, _start, At(tx, y), scale,
				new Color(1, 1, 1, TextAlpha(now)));
		}

		if (_small is not null && _legal.Length > 0)
		{
			// Centred on x 640 (sub_8235ABF0 draws it at (640,592), flags 4); a translation longer
			// than the guest's 1024-wide box wraps inside it.
			float legalWidth = Engine_UiCanvasText.Measure(_small, _legal);
			if (legalWidth <= 1024f)
			{
				Engine_UiCanvasText.DrawLine(this, _small, _legal, At(640f - (legalWidth * 0.5f), 592), scale, Colors.White);
			}
			else
			{
				Engine_UiCanvasText.DrawWrapped(this, _small, _legal, At(128, 592), 1024f, scale, Colors.White);
			}
		}
	}

	private void Quad(Texture2D texture, Vector2 a, Vector2 b, Vector2 c, Vector2 d,
		Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud, Color colour) =>
		DrawPolygon(new[] { a, b, c, d }, new[] { colour, colour, colour, colour }, new[] { ua, ub, uc, ud }, texture);
}
