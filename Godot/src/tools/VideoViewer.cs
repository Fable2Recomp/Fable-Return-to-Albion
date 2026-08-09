using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Refii.Core;
using Refii.Text;
using Refii.Ui;
using Refii.Video;

namespace Refii.Launcher.Tools;

/// <summary>
/// Browses and plays the game's .bik movies, the way model_viewer does for geometry.
///
/// Loads through <c>ResourceLoader</c> with a <c>game://</c> path, so it exercises the
/// registered loader rather than a private code path — if this plays, the format is
/// genuinely browsable like a .mdl or a .tex.
///
/// <c>N</c>/<c>P</c> step through movies, <c>space</c> restarts the current one.
/// </summary>
public partial class VideoViewer : Node
{
	/// <summary>Movie to open. <c>REFII_VIDEO</c> overrides; a path fragment is enough.</summary>
	[Export] public string VideoPath { get; set; } = string.Empty;

	private readonly List<string> _catalogue = new();
	private int _index;

	private TextureRect _view = null!;
	private Label _caption = null!;
	private ShaderMaterial _material = null!;

	private Engine_BinkVideo? _video;
	private Engine_BinkDecoder? _decoder;
	private double _clock;
	private int _frame;

	private ImageTexture? _luma;
	private ImageTexture? _chromaU;
	private ImageTexture? _chromaV;
	private Image? _lumaImage;
	private Image? _chromaUImage;
	private Image? _chromaVImage;
	private byte[] _y = Array.Empty<byte>();
	private byte[] _u = Array.Empty<byte>();
	private byte[] _v = Array.Empty<byte>();

	private string _shot = string.Empty;
	private Engine_BnkVideoLoader? _loader;
	private Engine_VideoLibrary _library = new();

	public override void _Ready()
	{
		_shot = OS.GetEnvironment("REFII_VIDEO_VIEW_SHOT");

		string root = Engine_GameData.Resolve();
		if (root.Length == 0)
		{
			GD.Print("no game data; set REFII_GAME_DATA.");
			GetTree().Quit(1);
			return;
		}

		// The videos folder, indexed on its own — one directory listing, instant. The banks
		// are not mounted here at all: no .bik is in them.
		_library = Engine_VideoLibrary.Load(root);
		Engine_BnkVideoLoader.SetLibrary(_library);
		GD.Print($"[video-viewer] {_library.Count} movies in " +
			$"{(_library.Directory.Length == 0 ? "(no videos folder)" : _library.Directory)}");

		if (_library.Count == 0)
		{
			GD.Print("no .bik files found.");
			GetTree().Quit(1);
			return;
		}

		_catalogue.AddRange(_library.Names);

		if (_loader is null)
		{
			_loader = new Engine_BnkVideoLoader();
			ResourceLoader.AddResourceFormatLoader(_loader);
		}

		BuildScene();

		string wanted = OS.GetEnvironment("REFII_VIDEO");
		if (wanted.Length == 0)
		{
			wanted = VideoPath;
		}

		if (wanted.Length != 0)
		{
			int found = _catalogue.FindIndex(
				p => p.Contains(wanted, StringComparison.OrdinalIgnoreCase));
			_index = found >= 0 ? found : 0;
		}

		Show(_index);
	}

	private void BuildScene()
	{
		_material = new ShaderMaterial
		{
			Shader = GD.Load<Shader>("res://src/Engine/video/bink_yuv.gdshader"),
		};

		_view = new TextureRect
		{
			Name = "Video",
			ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
			StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
			Material = _material,
		};
		_view.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		AddChild(_view);

		_caption = new Label { Position = new Vector2(12.0f, 8.0f) };
		_caption.AddThemeColorOverride("font_color", new Color(0.95f, 0.95f, 0.98f));
		_caption.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0));
		_caption.AddThemeConstantOverride("outline_size", 4);
		AddChild(_caption);
	}

	private void Show(int index)
	{
		_index = ((index % _catalogue.Count) + _catalogue.Count) % _catalogue.Count;
		string relative = _catalogue[_index];

		_decoder?.Dispose();
		_decoder = null;

		// Through the registered loader, deliberately: this is the thing being tested.
		_video = ResourceLoader.Load<Engine_BinkVideo>($"game://art/videos/{relative}.bik");
		if (_video is null)
		{
			_caption.Text = $"[{_index + 1}/{_catalogue.Count}] {relative}\nFAILED to load";
			GD.Print($"[video-viewer] {relative}: load failed");
			return;
		}

		_decoder = _video.CreateDecoder(out string error);
		if (_decoder is null)
		{
			_caption.Text = $"[{_index + 1}/{_catalogue.Count}] {relative}\n{error}";
			return;
		}

		Allocate(_decoder);
		_clock = 0.0;
		_frame = 0;

		_caption.Text =
			$"[{_index + 1}/{_catalogue.Count}] {relative}\n" +
			$"{_video.Width}x{_video.Height}  {_video.FrameCount} frames  " +
			$"{1.0 / _video.FrameSeconds:F2} fps  {_video.Duration:F1}s";
		GD.Print($"[video-viewer] {relative}: {_video.Width}x{_video.Height}, " +
			$"{_video.FrameCount} frames @ {1.0 / _video.FrameSeconds:F2} fps");
	}

	public override void _Process(double delta)
	{
		if (_decoder is null || _video is null)
		{
			return;
		}

		_clock += delta;
		if (_clock < _video.FrameSeconds)
		{
			return;
		}

		_clock -= _video.FrameSeconds;

		if (!_decoder.NextFrame(_y, _u, _v, out int cw, out int chh))
		{
			// Loop, so a short logo stays visible instead of vanishing on the last frame.
			_decoder.Rewind();
			_frame = 0;
			return;
		}

		_lumaImage!.SetData(_video.Width, _video.Height, false, Image.Format.R8, _y);
		_chromaUImage!.SetData(cw, chh, false, Image.Format.R8, _u);
		_chromaVImage!.SetData(cw, chh, false, Image.Format.R8, _v);
		_luma!.Update(_lumaImage);
		_chromaU!.Update(_chromaUImage);
		_chromaV!.Update(_chromaVImage);

		if (++_frame == 30 && _shot.Length != 0)
		{
			CallDeferred(nameof(SaveShot));
		}
	}

	private void SaveShot()
	{
		Image image = GetViewport().GetTexture().GetImage();
		Directory.CreateDirectory(Path.GetDirectoryName(_shot)!);
		GD.Print($"[video-viewer] shot -> {_shot} ({image.SavePng(_shot)})");
		GetTree().Quit(0);
	}

	private void Allocate(Engine_BinkDecoder decoder)
	{
		int cw = (decoder.Width + 1) / 2;
		int ch = (decoder.Height + 1) / 2;

		// Sized exactly: Image.SetData rejects a buffer that does not match the image, and
		// a rejected chroma upload renders as a pure green picture.
		_y = new byte[decoder.Width * decoder.Height];
		_u = new byte[cw * ch];
		_v = new byte[cw * ch];

		_lumaImage = Image.CreateEmpty(decoder.Width, decoder.Height, false, Image.Format.R8);
		_chromaUImage = Image.CreateEmpty(cw, ch, false, Image.Format.R8);
		_chromaVImage = Image.CreateEmpty(cw, ch, false, Image.Format.R8);
		_luma = ImageTexture.CreateFromImage(_lumaImage);
		_chromaU = ImageTexture.CreateFromImage(_chromaUImage);
		_chromaV = ImageTexture.CreateFromImage(_chromaVImage);

		_view.Texture = _luma;
		_material.SetShaderParameter("luma", _luma);
		_material.SetShaderParameter("chroma_r", _chromaV);
		_material.SetShaderParameter("chroma_b", _chromaU);
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is not InputEventKey { Pressed: true } key)
		{
			return;
		}

		switch (key.Keycode)
		{
			case Key.N: Show(_index + 1); break;
			case Key.P: Show(_index - 1); break;
			case Key.Space: _decoder?.Rewind(); break;
			case Key.Escape: GetTree().Quit(0); break;
		}
	}

	public override void _ExitTree() => _decoder?.Dispose();
}
