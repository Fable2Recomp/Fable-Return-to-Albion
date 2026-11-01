using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Ui;

/// <summary>
/// The controller's button art, swapped for keycaps while the keyboard and mouse are the
/// active device and back the instant the controller is touched.
/// </summary>
/// <remarks>
/// EVERY BUTTON GLYPH IS ONE TEXTURE OBJECT. The UI loads all of them through
/// <see cref="Engine_UiTextureCache.Load"/> — inline `&lt;a_img&gt;` icons, the .bgf prompt
/// quads cut from the `abyx` atlas, the HUD's world prompts — and every material, clone and
/// pattern book holds the texture it was given. So the cache hands out one proxy per glyph path
/// and the switch rewrites the proxy's pixels: nothing that drew a glyph has to redraw it.
///
/// A keycap is drawn at the original's size, so the UV insets the quads were cut with still
/// fit. The `abyx` atlas keeps everything but its eight button cells — its lower half is the
/// hold-meter ring. Labels come from the game (<see cref="LabelFor"/>), which knows the bindings
/// and whether a menu or the world has the input.
/// </remarks>
public static partial class Engine_UiKeyGlyphs
{
	/// <summary>The key a pad button stands for now: "A" → "E", "B" → "Esc" in a menu.</summary>
	public static Func<string, string?>? LabelFor { get; set; }

	/// <summary>The font keycaps are lettered in — the game's own, once the UI has loaded it.</summary>
	public static Engine_UiFontLibrary? Fonts { get; set; }

	private sealed class Proxy
	{
		public required ImageTexture Texture;
		public required Texture2D Original;
		public required string Button;
	}

	private static readonly Dictionary<string, Proxy> _proxies = new(StringComparer.Ordinal);

	private static string _drawnSignature = string.Empty;

	/// <summary>The buttons the `abyx` atlas's columns show, idle and pressed rows alike.</summary>
	private static readonly string[] AtlasColumns = { "A", "B", "Y", "X" };

	/// <summary>One keycap texture per pad button, redrawn when its label changes.</summary>
	private static readonly Dictionary<string, ImageTexture> _keycaps = new(StringComparer.Ordinal);

	private const int KeycapSize = 128;

	/// <summary>Every pad button a prompt can show, in the order the redraw signature lists them.</summary>
	private static readonly string[] Buttons =
		{ "A", "B", "X", "Y", "LB", "RB", "LT", "RT", "Start", "Back", "DPad", "LS", "RS" };

	/// <summary>The keycap a quad carries for <paramref name="button"/> — one object per button.</summary>
	public static ImageTexture KeycapFor(string button)
	{
		if (!_keycaps.TryGetValue(button, out ImageTexture? keycap))
		{
			keycap = ImageTexture.CreateFromImage(Keycap(button));
			_keycaps[button] = keycap;
		}

		return keycap;
	}

	private static Image Keycap(string button)
	{
		var canvas = new Canvas(KeycapSize, KeycapSize);
		DrawKeycap(canvas, new Rect2I(0, 0, KeycapSize, KeycapSize),
			LabelFor?.Invoke(button) ?? button, pressed: false);
		return canvas.ToImage(mipmaps: true);
	}

	/// <summary>
	/// Which pad button a quad cut from the `abyx` atlas draws, or null for anything else (the
	/// atlas's lower half is the hold-meter ring). The element's own name says it where it can
	/// — `A`, `X`, `icon_button_y`, `AButton`, `y_button` — up to three ancestors, since the
	/// prompt group is named and the quad under it often is not; the atlas column otherwise.
	/// </summary>
	public static string? AtlasButton(string texturePath, UiNode node, float uMin, float vMax)
	{
		string path = texturePath.Replace('\\', '/').ToLowerInvariant();
		if (!path.EndsWith("art/gui/persistant/abyx.tex", StringComparison.Ordinal) || vMax > 0.51f)
		{
			return null;
		}

		UiNode? at = node;
		for (int hop = 0; at is not null && hop <= 3; ++hop, at = at.Parent)
		{
			if (ButtonNamed(at.Name) is { } named)
			{
				return named;
			}
		}

		int column = Math.Clamp((int)Math.Floor((uMin * 4f) + 0.01f), 0, 3);
		return AtlasColumns[column];
	}

	private static string? ButtonNamed(string name)
	{
		string n = name.ToLowerInvariant();
		foreach (string b in new[] { "a", "b", "x", "y" })
		{
			if (n == b || n == b + "button" || n == b + "_button" || n == "button_" + b ||
				n.StartsWith("icon_button_" + b, StringComparison.Ordinal) && n.Length <= 14 + 2)
			{
				return b.ToUpperInvariant();
			}
		}

		return null;
	}

	/// <summary>
	/// The texture to hand out for a path: a proxy for a button glyph, the texture itself for
	/// anything else.
	/// </summary>
	public static Texture2D Wrap(string virtualPath, Texture2D original)
	{
		string key = virtualPath.Replace('\\', '/').ToLowerInvariant();
		if (key.EndsWith("art/gui/persistant/abyx.tex", StringComparison.Ordinal))
		{
			PadAtlas = original;
		}

		if (ButtonFor(key) is not { } button)
		{
			return original;
		}

		if (_proxies.TryGetValue(key, out Proxy? known))
		{
			return known.Texture;
		}

		Image? image = Refii.Core.Engine_ServerSyncs.Image(original);
		if (image is null || image.IsEmpty())
		{
			return original;
		}

		var proxy = new Proxy
		{
			Texture = ImageTexture.CreateFromImage(image),
			Original = original,
			Button = button,
		};
		_proxies[key] = proxy;
		Draw(proxy, Engine_UiInputMode.Keycaps);

		// The enhancer swaps a sharper image into the original later; the pad art follows it.
		// (Keyboard mode keeps its keycap — the atlas cells are redrawn on the next switch.)
		original.Changed += () =>
		{
			if (!Engine_UiInputMode.Keycaps)
			{
				Draw(proxy, Engine_UiInputMode.Keycaps);
			}
		};

		return proxy.Texture;
	}

	/// <summary>
	/// Redraws every glyph when the device or a label has changed. Once a frame; cheap when
	/// nothing has.
	/// </summary>
	public static void Update()
	{
		bool keys = Engine_UiInputMode.Keycaps;
		string signature = keys ? "kbm" : "pad";
		if (keys)
		{
			foreach (string button in Buttons)
			{
				signature += "|" + (LabelFor?.Invoke(button) ?? "?");
			}
		}

		if (signature == _drawnSignature)
		{
			return;
		}

		_drawnSignature = signature;
		RenderingServer.GlobalShaderParameterSet("ui_keyboard_mode", keys);
		foreach (Proxy proxy in _proxies.Values)
		{
			Draw(proxy, keys);
		}

		if (keys)
		{
			foreach ((string button, ImageTexture keycap) in _keycaps)
			{
				keycap.SetImage(Keycap(button));
			}
		}
	}

	/// <summary>Which pad button a texture path draws, or null for any other art.</summary>
	private static string? ButtonFor(string path)
	{
		const string Controller = "art/gui/controller/";
		int at = path.IndexOf(Controller, StringComparison.Ordinal);
		if (at < 0)
		{
			return null;
		}

		// `24x24/AnimLeftLeft` is the same stick as `LeftLeft`, drawn as frames.
		string name = System.IO.Path.GetFileNameWithoutExtension(path[(at + Controller.Length)..]);
		if (name.StartsWith("anim", StringComparison.Ordinal))
		{
			name = name["anim".Length..];
		}

		return name switch
		{
			"icon_button_a" => "A",
			"icon_button_b" => "B",
			"icon_button_x" => "X",
			"icon_button_y" => "Y",
			"icon_button_lt" or "lefttrigger" => "LT",
			"righttrigger" => "RT",
			"leftbutton" => "LB",
			"rightbutton" => "RB",
			"start" => "Start",
			"back" => "Back",
			"dpad" => "DPad",
			"left" or "leftup" or "leftdown" or "leftleft" or "leftright" => "LS",
			"right" or "rightup" or "rightdown" or "rightleft" or "rightright" => "RS",
			_ => null,
		};
	}

	private static void Draw(Proxy proxy, bool keys)
	{
		Image? pad = Refii.Core.Engine_ServerSyncs.Image(proxy.Original);
		if (pad is null || pad.IsEmpty())
		{
			return;
		}

		if (!keys)
		{
			proxy.Texture.SetImage(pad);
			return;
		}

		// One keycap per square cell along the long side, so an animated strip shows a whole key
		// in whichever frame its UVs select; a square texture is one cell.
		int width = pad.GetWidth();
		int height = pad.GetHeight();
		int side = Math.Min(width, height);
		string label = LabelFor?.Invoke(proxy.Button) ?? proxy.Button;
		var canvas = new Canvas(width, height);
		for (int y = 0; y + side <= height; y += side)
		{
			for (int x = 0; x + side <= width; x += side)
			{
				DrawKeycap(canvas, new Rect2I(x, y, side, side), label, pressed: false);
			}
		}

		proxy.Texture.SetImage(canvas.ToImage(pad.HasMipmaps()));
	}

	/// <summary>
	/// An RGBA8 image drawn in memory and handed to Godot once. Each pixel is stored exactly as
	/// <c>Image.SetPixel</c> stores it and read back exactly as <c>Image.GetPixel</c> reads it
	/// (core/io/image.cpp <c>_set_color_at_ofs</c>/<c>_get_color_at_ofs</c>, FORMAT_RGBA8), so
	/// the keycap is the same picture a pixel-at-a-time draw gave — without a native call per
	/// pixel, which was tens of milliseconds for a redraw of every prompt on a device switch.
	/// </summary>
	private sealed class Canvas
	{
		public readonly int Width;
		public readonly int Height;
		public readonly byte[] Data;

		public Canvas(int width, int height)
		{
			Width = width;
			Height = height;
			Data = new byte[width * height * 4];
		}

		public Color Get(int x, int y)
		{
			int at = ((y * Width) + x) * 4;
			return new Color(
				(float)(Data[at] / 255.0),
				(float)(Data[at + 1] / 255.0),
				(float)(Data[at + 2] / 255.0),
				(float)(Data[at + 3] / 255.0));
		}

		public void Set(int x, int y, Color colour)
		{
			int at = ((y * Width) + x) * 4;
			Data[at] = Channel(colour.R);
			Data[at + 1] = Channel(colour.G);
			Data[at + 2] = Channel(colour.B);
			Data[at + 3] = Channel(colour.A);
		}

		/// <summary>Clears a cell to transparent black, as <c>FillRect</c> with (0,0,0,0) does.</summary>
		public void Clear(Rect2I cell)
		{
			int x0 = Math.Max(0, cell.Position.X);
			int x1 = Math.Min(Width, cell.End.X);
			int y0 = Math.Max(0, cell.Position.Y);
			int y1 = Math.Min(Height, cell.End.Y);
			for (int y = y0; y < y1; ++y)
			{
				Array.Clear(Data, ((y * Width) + x0) * 4, Math.Max(0, x1 - x0) * 4);
			}
		}

		public Image ToImage(bool mipmaps)
		{
			Image image = Image.CreateFromData(Width, Height, false, Image.Format.Rgba8, Data);
			if (mipmaps)
			{
				image.GenerateMipmaps();
			}

			return image;
		}

		/// <summary><c>uint8_t(CLAMP(c * 255.0, 0, 255))</c>: scaled in double, truncated.</summary>
		private static byte Channel(float c) => (byte)Math.Clamp(c * 255.0, 0.0, 255.0);
	}

	// The touch controls' plate and trim colours, PadAtlas and TextImage: android/Engine_UiKeyGlyphs.Touch.cs.

	private static readonly Color CapFill = new(0.10f, 0.09f, 0.07f, 0.92f);
	private static readonly Color CapRim = new(1f, 224f / 255f, 128f / 255f, 1f);
	private static readonly Color CapPressed = new(0.22f, 0.19f, 0.12f, 0.95f);

	/// <summary>A rounded key with the label centred in the game's font, filling one cell.</summary>
	private static void DrawKeycap(Canvas image, Rect2I cell, string label, bool pressed)
	{
		image.Clear(cell);

		// No key for this prompt here: the cell stays empty rather than showing a blank cap.
		if (label.Length == 0)
		{
			return;
		}

		int inset = Math.Max(1, cell.Size.X / 16);
		var cap = new Rect2I(cell.Position + new Vector2I(inset, inset), cell.Size - new Vector2I(inset * 2, inset * 2));
		float radius = Math.Min(cap.Size.X, cap.Size.Y) * 0.22f;
		float rim = Math.Max(1f, cap.Size.X / 20f);

		for (int y = cap.Position.Y; y < cap.End.Y; ++y)
		{
			for (int x = cap.Position.X; x < cap.End.X; ++x)
			{
				float d = RoundedDistance(x + 0.5f, y + 0.5f, cap, radius);
				if (d > 0.5f)
				{
					continue;
				}

				float edge = Mathf.Clamp(0.5f - d, 0f, 1f);
				Color colour = d > -rim ? CapRim : pressed ? CapPressed : CapFill;
				image.Set(x, y, new Color(colour.R, colour.G, colour.B, colour.A * edge));
			}
		}

		DrawLabel(image, cap, label);
	}

	/// <summary>Signed distance to a rounded rectangle's edge, negative inside.</summary>
	private static float RoundedDistance(float x, float y, Rect2I rect, float radius)
	{
		float cx = rect.Position.X + (rect.Size.X / 2f);
		float cy = rect.Position.Y + (rect.Size.Y / 2f);
		float qx = Math.Abs(x - cx) - ((rect.Size.X / 2f) - radius);
		float qy = Math.Abs(y - cy) - ((rect.Size.Y / 2f) - radius);
		float outside = new Vector2(Math.Max(qx, 0f), Math.Max(qy, 0f)).Length();
		return outside + Math.Min(Math.Max(qx, qy), 0f) - radius;
	}

	/// <summary>The decoded font atlas as coverage, cached per font: its red channel, one byte a texel.</summary>
	private static byte[]? _atlas;
	private static int _atlasWidth;
	private static int _atlasHeight;
	private static int _atlasStride;
	private static Engine_BitmapFont? _atlasFont;

	/// <summary>Decodes a font's atlas into coverage once per font (<see cref="_atlas"/>).</summary>
	private static void EnsureFontAtlas(Engine_BitmapFont font)
	{
		if (!ReferenceEquals(_atlasFont, font))
		{
			_atlasFont = font;
			_atlas = null;
			Image decoded = Image.CreateFromData(font.AtlasWidth, font.AtlasHeight, false,
				Image.Format.RgtcR, Engine_BitmapFontTexture.DecodeAtlasBlocks(font));
			if (!decoded.IsEmpty())
			{
				decoded.Decompress();
				int stride = decoded.GetFormat() switch
				{
					Image.Format.L8 or Image.Format.R8 => 1,
					Image.Format.La8 or Image.Format.Rg8 => 2,
					Image.Format.Rgb8 => 3,
					Image.Format.Rgba8 => 4,
					_ => 0,
				};
				if (stride == 0)
				{
					decoded.Convert(Image.Format.Rgba8);
					stride = 4;
				}

				_atlas = decoded.GetData();
				_atlasWidth = decoded.GetWidth();
				_atlasHeight = decoded.GetHeight();
				_atlasStride = stride;
			}
		}
	}

	private static void DrawLabel(Canvas image, Rect2I cap, string label)
	{
		if (Fonts?.For(18) is not { } selection || label.Length == 0)
		{
			return;
		}

		Engine_BitmapFont font = selection.Font.Font;
		EnsureFontAtlas(font);
		if (_atlas is null)
		{
			return;
		}

		float width = font.MeasureWidth(label);
		float height = font.LineHeight + 1;
		float scale = Math.Min(cap.Size.X * 0.78f / Math.Max(1f, width), cap.Size.Y * 0.62f / height);
		float penX = cap.Position.X + ((cap.Size.X - (width * scale)) / 2f);
		float top = cap.Position.Y + ((cap.Size.Y - (height * scale)) / 2f);

		foreach (char c in label)
		{
			BitmapGlyph g = font.GetGlyph(c);
			float left = penX + (g.LeftBearing * scale);
			penX = left + (g.PostAdvance * scale);
			if (g.DrawWidth <= 0)
			{
				continue;
			}

			BlitGlyph(image, g, left, top, g.DrawWidth * scale, height * scale, CapRim);
		}
	}

	/// <summary>Draws one glyph's coverage as the rim colour, sampled bilinearly.</summary>
	private static void BlitGlyph(Canvas image, BitmapGlyph g, float left, float top,
		float width, float height, Color colour)
	{
		int x0 = Math.Max(0, (int)Math.Floor(left));
		int x1 = Math.Min(image.Width, (int)Math.Ceiling(left + width));
		int y0 = Math.Max(0, (int)Math.Floor(top));
		int y1 = Math.Min(image.Height, (int)Math.Ceiling(top + height));
		int aw = _atlasWidth;
		int ah = _atlasHeight;

		for (int y = y0; y < y1; ++y)
		{
			float v = Mathf.Lerp(g.V0, g.V1, (y + 0.5f - top) / height);
			for (int x = x0; x < x1; ++x)
			{
				float u = Mathf.Lerp(g.U0, g.U1, (x + 0.5f - left) / width);
				float coverage = Sample((u * aw) - 0.5f, (v * ah) - 0.5f);
				if (coverage <= 0.01f)
				{
					continue;
				}

				Color under = image.Get(x, y);
				Color blended = under.Lerp(colour, coverage);
				blended.A = Math.Max(under.A, coverage);
				image.Set(x, y, blended);
			}
		}
	}

	private static float Sample(float x, float y)
	{
		int xi = (int)Math.Floor(x);
		int yi = (int)Math.Floor(y);
		float fx = x - xi;
		float fy = y - yi;
		float a = Texel(xi, yi);
		float b = Texel(xi + 1, yi);
		float c = Texel(xi, yi + 1);
		float d = Texel(xi + 1, yi + 1);
		return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
	}

	/// <summary>The atlas's red channel at a texel as <c>GetPixel(x, y).R</c> read it, 0 outside.</summary>
	private static float Texel(int x, int y) =>
		x < 0 || y < 0 || x >= _atlasWidth || y >= _atlasHeight
			? 0f
			: (float)(_atlas![((y * _atlasWidth) + x) * _atlasStride] / 255.0);
}
