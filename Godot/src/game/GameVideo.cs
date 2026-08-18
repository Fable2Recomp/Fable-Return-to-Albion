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

	/// <summary>How many were ended early by the player rather than running out of frames.</summary>
	public int MoviesSkipped { get; private set; }

	/// <summary>
	/// Whether Godot ends a movie when the GUEST says it was skipped. On unless
	/// <c>REFII_VIDEO_FOLLOW_SKIP=0</c>.
	/// </summary>
	private static readonly bool FollowGuestSkip =
		OS.GetEnvironment("REFII_VIDEO_FOLLOW_SKIP") != "0";

	/// <summary>
	/// Ends the movie on screen NOW, as if it had reached its last frame.
	///
	/// Godot owns playback, so a skip is Godot stopping the picture and then telling the
	/// guest — in that order. The guest is told through the SAME byte its own
	/// <c>Render_DrawBinkMovieFrame</c> writes when a movie runs out
	/// (<c>bink+221</c>, via <see cref="HostBridge.FinishMovie"/>), which is why a skip needs
	/// no separate mechanism and cannot leave the guest's startup sequence waiting: to the
	/// guest, a skipped movie and a finished one are the same event.
	///
	/// NOT a decision about whether skipping is ALLOWED. That is the guest's contract and the
	/// caller's business; this only carries it out. Returns false when nothing was playing.
	/// </summary>
	public bool Skip(string reason)
	{
		if (_decoder is null)
		{
			return false;
		}

		_decoder.Dispose();
		_decoder = null;
		_view.Visible = false;
		++MoviesSkipped;

		uint retired = HostBridge.FinishMovie();
		GD.Print($"[game-video] skipped ({reason})" +
			(retired != 0
				? $"; retired the guest's player 0x{retired:X8}"
				: "; NO LIVE GUEST PLAYER — the guest had already retired it"));
		EmitSignal(SignalName.MovieFinished);
		return true;
	}

	private TextureRect _view = null!;
	private ShaderMaterial _material = null!;

	private Engine_BinkDecoder? _decoder;
	private uint _lastRequest;
	private double _frameClock;

	/// <summary>
	/// Most frames decoded in one host frame while catching up.
	///
	/// Bounded so a long stall cannot turn into a decode spiral that makes the next host
	/// frame longer still. Four is roughly a 7 fps floor against a 30 fps movie; below that
	/// the clock is reset rather than chased.
	/// </summary>
	private const int MaxCatchUpFrames = 4;

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

		// BLACK, not the window's clear colour, behind the letterboxed movie: the bars around a
		// 16:9 bink in a non-16:9 window read as gray without it.
		var backdrop = new ColorRect
		{
			Name = "VideoBackdrop",
			Color = Colors.Black,
		};
		backdrop.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		layer.AddChild(backdrop);
		_backdrop = backdrop;

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

		// THE SAME DECODE, A SECOND DESTINATION: the same YUV planes and material, drawn into a
		// UI element's rect instead of the screen. The SubViewport makes it a sampleable
		// Texture2D.
		_embedded = new SubViewport
		{
			Name = "VideoEmbed",
			RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
			RenderTargetClearMode = SubViewport.ClearMode.Always,
			TransparentBg = false,
			Disable3D = true,
			Size = new Vector2I(2, 2),
		};
		AddChild(_embedded);

		// STRETCHED, never letterboxed: the guest's draw (0x82A41F10) never reads the movie's
		// own width or height.
		_embeddedView = new TextureRect
		{
			Name = "EmbedView",
			ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
			StretchMode = TextureRect.StretchModeEnum.Scale,
			Material = _material,
		};
		_embeddedView.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		_embedded.AddChild(_embeddedView);
	}

	private SubViewport _embedded = null!;
	private TextureRect _embeddedView = null!;
	private ColorRect _backdrop = null!;

	/// <summary>
	/// Where the movie is going: an element ordinal, or -1 for the screen.
	///
	/// Set by <see cref="Refii.Game.GameUi"/> from the guest's own statement — a sprite whose
	/// `Movie` string is non-empty hosts the movie in its own rect — because the presentation
	/// and its ordinal map live there and not here.
	/// </summary>
	public int EmbedOrdinal { get; set; } = -1;

	/// <summary>The decoded frame as a texture a UI quad can sample, or null when not playing.</summary>
	public Texture2D? FrameTexture =>
		_decoder is null || EmbedOrdinal < 0 ? null : _embedded.GetTexture();

	public override void _Process(double delta)
	{
		// The letterbox backdrop follows the movie's own visibility — every site that toggles
		// _view also owns this, but mirroring it here cannot miss one.
		_backdrop.Visible = _view.Visible;

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

	/// <summary>Takes the current movie off screen and releases its decoder.</summary>
	private void StopVideo()
	{
		_decoder?.Dispose();
		_decoder = null;
		_view.Visible = false;
		EmitSignal(SignalName.MovieFinished);
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

		// DRAIN the clock, do not consume one frame from it.
		//
		// Decoding exactly one frame per host frame and subtracting one frame's worth cannot
		// keep time. Whenever a host frame takes longer than a movie frame — a 30 fps movie
		// and any dip below 30 fps, which streaming a region reliably causes — the accumulator
		// grows and never shrinks, so the movie plays SLOWER THAN REAL TIME and drifts further
		// behind for as long as it runs, with no path that decodes two frames in one.
		//
		// That drift is the Bink desync. The guest decodes the same movie on ITS own wall
		// clock to decide when its startup sequence may continue — this class then reports
		// the movie finished, and a picture running late reports late. The guest was waiting
		// on our clock, and our clock was slow.
		//
		// So: decode until caught up, and upload only the frame that actually lands on
		// screen. Intermediate frames are dropped, which is what a video player does when it
		// is behind; showing every frame late is the one option that keeps the audio, the
		// guest and the picture disagreeing.
		int chromaWidth = 0;
		int chromaHeight = 0;
		int decoded = 0;
		bool ended = false;

		// THE GUEST MAY HAVE ENDED THIS MOVIE ALREADY, and a skip is how.
		//
		// Skippability is the GUEST's to decide and it already does: the player carries a
		// threshold and an elapsed accumulator, and its own skip sites set the flag when the
		// button is pressed and enough of the movie has run — five seconds for the startup
		// logos, immediately for the attract clip, and never for an in-game cutscene, which
		// has no such field at all. Since Godot feeds the real button press through XInput,
		// all of that enforcement happens without the host deciding anything.
		//
		// What the host has to do is NOTICE. Godot owns the picture, so a movie the guest has
		// retired keeps playing over whatever it moved on to unless we stop. This is that
		// stop — and it deliberately does not consult a filename or a duration of our own.
		// REVERSIBLE ON PURPOSE. This is new, and "the world is showing where the movie should
		// be" is exactly what a spurious end looks like — so it can be turned off in one run
		// rather than argued about. REFII_VIDEO_FOLLOW_SKIP=0 leaves Godot ending movies only
		// when its own decode runs out, which is the behaviour that was working before.
		if (FollowGuestSkip && HostBridge.MovieState() == HostBridge.MoviePlayback.EndedByGuest)
		{
			Skip("the guest skipped it");
			return;
		}

		while (_frameClock >= _decoder.FrameSeconds && decoded < MaxCatchUpFrames)
		{
			_frameClock -= _decoder.FrameSeconds;
			if (!_decoder.NextFrame(_y, _u, _v, out chromaWidth, out chromaHeight))
			{
				ended = true;
				break;
			}

			++decoded;
		}

		// A pause long enough to exceed the catch-up cap — a level load, a breakpoint — must
		// not leave the clock holding seconds of debt that then plays out as a fast-forward.
		// Dropping the remainder resynchronises to now, which is where the guest already is.
		if (_frameClock >= _decoder.FrameSeconds)
		{
			_frameClock = 0.0;
		}

		if (ended)
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

		if (decoded == 0)
		{
			return;  // caught up without a new frame — nothing to upload
		}

		_lumaImage!.SetData(_decoder.Width, _decoder.Height, false, Image.Format.R8, _y);
		_chromaUImage!.SetData(chromaWidth, chromaHeight, false, Image.Format.R8, _u);
		_chromaVImage!.SetData(chromaWidth, chromaHeight, false, Image.Format.R8, _v);

		// Updated in place — a fresh texture per frame would churn three VRAM allocations
		// at video rate.
		_luma!.Update(_lumaImage);
		_chromaU!.Update(_chromaUImage);
		_chromaV!.Update(_chromaVImage);

		// An embedded movie must not also paint the screen — the two share one material.
		_embeddedView.Texture = _luma;
		_view.Visible = EmbedOrdinal < 0;

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

		_embedded.Size = new Vector2I(decoder.Width, decoder.Height);
		_view.Texture = _luma;
		_material.SetShaderParameter("luma", _luma);
		_material.SetShaderParameter("chroma_r", _chromaV);
		_material.SetShaderParameter("chroma_b", _chromaU);
	}

	public override void _ExitTree() => _decoder?.Dispose();
}
