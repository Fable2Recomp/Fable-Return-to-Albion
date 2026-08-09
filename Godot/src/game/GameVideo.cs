using System;
using Godot;
using Refii.Host;
using Refii.Core;
using Refii.Text;
using Refii.Ui;
using Refii.Video;

namespace Refii.Game;

/// <summary>
/// Plays the game's Bink videos — decoded by GODOT, driven by the guest.
///
/// The guest still decides WHEN a video plays and WHICH one: the movie-open hook reports
/// the file it asked BinkOpen for, and this plays that file. But the decoding is ours,
/// with FFmpeg, so nothing here depends on the guest reaching its render path, on the
/// double-buffered planes it decodes into, or on the render thread that is currently
/// parked in sub_822DFE08. Scraping those planes worked, but only while all three held.
///
/// Planes go to the GPU untouched and a shader converts them, which is cheaper than a CPU
/// conversion and gives chroma real filtering rather than nearest-neighbour steps.
/// </summary>
public partial class GameVideo : Node
{
	/// <summary>A movie has started. The argument is its frame count.</summary>
	[Signal] public delegate void MovieStartedEventHandler(int frames);

	/// <summary>The current movie ran out of frames.</summary>
	[Signal] public delegate void MovieFinishedEventHandler();

	/// <summary>True while a movie is on screen.</summary>
	public bool IsPlaying => _decoder is not null;

	/// <summary>How many movies have played to the end this session.</summary>
	public int MoviesFinished { get; private set; }

	/// <summary>How many the guest has asked for, finished or not.</summary>
	public int MoviesRequested { get; private set; }

	private TextureRect _view = null!;
	private ShaderMaterial _material = null!;

	private Engine_BinkDecoder? _decoder;
	private uint _lastRequest;
	private double _frameClock;

	private ImageTexture? _luma;
	private ImageTexture? _chromaU;
	private ImageTexture? _chromaV;
	private Image? _lumaImage;
	private Image? _chromaUImage;
	private Image? _chromaVImage;
	private byte[] _y = Array.Empty<byte>();
	private byte[] _u = Array.Empty<byte>();
	private byte[] _v = Array.Empty<byte>();

	public override void _Ready()
	{
		var layer = new CanvasLayer { Name = "VideoLayer", Layer = 10 };
		AddChild(layer);

		_material = new ShaderMaterial
		{
			Shader = GD.Load<Shader>("res://src/Engine/video/bink_yuv.gdshader"),
		};

		_view = new TextureRect
		{
			Name = "VideoView",
			// Letterboxed, never stretched: the logos are 16:9 and the window may not be.
			ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
			StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
			Material = _material,
			Visible = false,
		};
		_view.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		layer.AddChild(_view);
	}

	public override void _Process(double delta)
	{
		if (HostBridge.TryGetAbiVersion() is null)
		{
			return;
		}

		byte[]? requested = HostBridge.GetRequestedVideo(out uint generation);
		if (generation != _lastRequest)
		{
			_lastRequest = generation;

			// A request carries EITHER the loaded file or its path. The logos come as
			// bytes the guest already read; everything after them is opened by name, and
			// the guest never reads those itself — so the host resolves them.
			if (requested is null)
			{
				string path = HostBridge.GetRequestedVideoName(out _);
				requested = path.Length == 0 ? null : ResolveMovie(path);
			}

			StartVideo(requested);
		}

		AdvanceVideo(delta);
	}

	/// <summary>
	/// Reads a movie the guest named rather than loaded.
	///
	/// Through the same library the editor's Videos folder uses, so a movie the guest
	/// asks for and a movie browsed by hand resolve identically. The guest's path is a
	/// console one — "game:\data\art\videos\x.bik" — and the library keys on the file
	/// name, so neither the device prefix nor the separators have to be translated.
	/// </summary>
	private byte[]? ResolveMovie(string guestPath)
	{
		_library ??= Engine_VideoLibrary.Load(Engine_GameData.Resolve());

		byte[]? bytes = _library.Read(guestPath);
		if (bytes is null)
		{
			GD.PushWarning($"[game-video] the guest asked for '{guestPath}', " +
				$"which is not in {_library.Directory}");
		}
		else
		{
			GD.Print($"[game-video] guest asked for '{guestPath}'");
		}

		return bytes;
	}

	private Engine_VideoLibrary? _library;

	/// <summary>
	/// Plays the .bik the guest just opened. The bytes come straight from guest memory —
	/// the game loads the file itself and hands BinkOpen a buffer, so there is no path to
	/// resolve and no second read of a file already loaded.
	/// </summary>
	private void StartVideo(byte[]? requested)
	{
		_decoder?.Dispose();
		_decoder = null;
		_view.Visible = false;

		if (requested is null || requested.Length == 0)
		{
			return;
		}

		_decoder = Engine_BinkDecoder.Open(requested, out string error);
		if (_decoder is null)
		{
			GD.PushWarning($"[game-video] could not decode the guest's movie: {error}");
			return;
		}

		Allocate(_decoder);
		_frameClock = 0.0;
		++MoviesRequested;
		GD.Print($"[game-video] playing {_decoder.Width}x{_decoder.Height}, " +
			$"{_decoder.FrameCount} frames @ {1.0 / _decoder.FrameSeconds:F2} fps");
		EmitSignal(SignalName.MovieStarted, _decoder.FrameCount);
	}

	private void AdvanceVideo(double delta)
	{
		if (_decoder is null)
		{
			return;
		}

		// Paced by the file's own frame rate rather than the display's: a 25 fps logo
		// stepped once per drawn frame would play at more than double speed.
		_frameClock += delta;
		if (_frameClock < _decoder.FrameSeconds)
		{
			return;
		}

		_frameClock -= _decoder.FrameSeconds;

		if (!_decoder.NextFrame(_y, _u, _v, out int chromaWidth, out int chromaHeight))
		{
			// End of the movie: stop drawing rather than freezing on the last frame.
			_decoder.Dispose();
			_decoder = null;
			_view.Visible = false;
			++MoviesFinished;

			// Tell the guest, or it never advances. Its startup sequence blocks until every
			// movie has played and decides that from its OWN Bink decode — output nobody
			// sees now, on a clock that drifts from this one. Retiring the movie here is
			// what keeps the sequence in step with the picture.
			uint retired = HostBridge.FinishMovie();
			GD.Print($"[game-video] finished" +
				(retired != 0 ? $"; retired the guest's player 0x{retired:X8}" : string.Empty));
			EmitSignal(SignalName.MovieFinished);
			return;
		}

		_lumaImage!.SetData(_decoder.Width, _decoder.Height, false, Image.Format.R8, _y);
		_chromaUImage!.SetData(chromaWidth, chromaHeight, false, Image.Format.R8, _u);
		_chromaVImage!.SetData(chromaWidth, chromaHeight, false, Image.Format.R8, _v);

		// Updated in place — a fresh texture per frame would churn three VRAM allocations
		// at video rate.
		_luma!.Update(_lumaImage);
		_chromaU!.Update(_chromaUImage);
		_chromaV!.Update(_chromaVImage);
		_view.Visible = true;

		// Deterministic capture: save once, a little way into playback, rather than
		// guessing a wall-clock moment. The startup videos begin at a time that moves
		// between runs, and timed shots kept landing on the gap between them.
		++_shownFrames;
		string shot = OS.GetEnvironment("REFII_VIDEO_FRAME_SHOT");
		if (shot.Length != 0 && _shownFrames == 40)
		{
			// Deferred: the texture updates above are not on screen until this frame is
			// actually drawn, so grabbing the viewport now would capture the previous one.
			CallDeferred(nameof(SaveShot), shot);
		}
	}

	private int _shownFrames;

	private void SaveShot(string path)
	{
		Image image = GetViewport().GetTexture().GetImage();
		System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
		GD.Print($"[game-video] frame shot -> {path} ({image.SavePng(path)})");
	}

	private void Allocate(Engine_BinkDecoder decoder)
	{
		// Buffers sized EXACTLY to their planes. Image.SetData requires the byte count to
		// match the image's own dimensions, so an over-sized chroma buffer is rejected and
		// the texture silently stays black — which renders as a pure green picture, since
		// zero chroma drives red and blue to the clamp and lifts green.
		int chromaWidth = (decoder.Width + 1) / 2;
		int chromaHeight = (decoder.Height + 1) / 2;

		_y = new byte[decoder.Width * decoder.Height];
		_u = new byte[chromaWidth * chromaHeight];
		_v = new byte[chromaWidth * chromaHeight];

		_lumaImage = Image.CreateEmpty(decoder.Width, decoder.Height, false, Image.Format.R8);
		_chromaUImage = Image.CreateEmpty(chromaWidth, chromaHeight, false, Image.Format.R8);
		_chromaVImage = Image.CreateEmpty(chromaWidth, chromaHeight, false, Image.Format.R8);

		_luma = ImageTexture.CreateFromImage(_lumaImage);
		_chromaU = ImageTexture.CreateFromImage(_chromaUImage);
		_chromaV = ImageTexture.CreateFromImage(_chromaVImage);

		_view.Texture = _luma;
		_material.SetShaderParameter("luma", _luma);
		_material.SetShaderParameter("chroma_r", _chromaV);
		_material.SetShaderParameter("chroma_b", _chromaU);
	}

	public override void _ExitTree() => _decoder?.Dispose();
}
