using System;
using Godot;
using Refii.Host;
using Refii.Core;
using Refii.Text;
using Refii.Ui;
using Refii.Video;

namespace Refii.Game;

/// <summary>
/// Plays the game's Bink videos: the guest states when and which (the movie-open hook reports
/// the file it passed BinkOpen), Godot decodes with FFmpeg. Nothing here depends on the guest
/// reaching its render path. YUV planes go to the GPU untouched; a shader converts them.
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
	/// Ends the movie on screen now, as if it had reached its last frame: stop the picture,
	/// then tell the guest through the same byte its <c>Render_DrawBinkMovieFrame</c> writes on
	/// a natural end (<c>bink+221</c>, via <see cref="HostBridge.FinishMovie"/>) — to the guest
	/// a skipped and a finished movie are one event. Does not decide whether skipping is
	/// allowed; that is the guest's contract. Returns false when nothing was playing.
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
	/// Most frames decoded in one host frame while catching up, so a stall cannot spiral.
	/// Four is roughly a 7 fps floor against a 30 fps movie; below that the clock is reset.
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

		// Black, not the window's clear colour: the bars around a 16:9 bink in a non-16:9
		// window read as gray without it.
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

		// Second destination for the same planes and material: a UI element's rect instead of
		// the screen. The SubViewport makes it a sampleable Texture2D.
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

		// Stretched, never letterboxed: the guest's draw (0x82A41F10) never reads the movie's
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
	/// Where the movie is going: an element ordinal, or -1 for the screen. Set by
	/// <see cref="Refii.Game.GameUi"/> — a sprite with a non-empty `Movie` string hosts it.
	/// </summary>
	public int EmbedOrdinal { get; set; } = -1;

	/// <summary>
	/// Whether the guest asked for this movie to cover the screen, from the rect it states after
	/// every open (`sub_82A41E30`, via <see cref="HostBridge.GetMovieRect"/>).
	///
	/// The rect is LEFT, TOP, RIGHT, BOTTOM — four ints converted to floats at B+8/+12/+16/+20
	/// (0x82a41e58 / e78 / e98 / eb8) — not x/y/w/h. Judged by WIDTH, because height cannot be
	/// the test: the two front-end owners both state the full backbuffer WIDTH and differ only
	/// in the height they letterbox to. The startup queue's constructor `sub_82389040` states
	/// `{0, 0, BackBufferWidth, BackBufferHeight}` unconditionally at 0x82389148, with no
	/// widescreen branch at all; the attract movie's `sub_82388ED0` states
	/// `{0, H/8, W, 7H/8}` in 4:3 and `{0, 0, W, H}` in widescreen. Only the cutscene player
	/// and the UI sprite's movie item state a sub-rect, and those are the embedded cases.
	///
	/// KNOWN LIMIT: the threshold is a fraction of the guest's 2D screen space while the rect
	/// is in BACKBUFFER units. They agree because the guest's canvas IS the backbuffer, but a
	/// backbuffer narrower than 960 would read every fullscreen movie as embedded. The guest
	/// rounds its width up to a multiple of 160 (`sub_82B6EBC0`), so this is a real edge rather
	/// than an impossible one.
	///
	/// No rect yet answers true rather than blanking the movie — and note the rect is written a
	/// few instructions AFTER the open, so "no rect yet" is not the only way to read one the
	/// guest has not stated; see the ordering note in the `sub_82A41B48` hook.
	/// </summary>
	public bool StatedFullscreen()
	{
		if (HostBridge.GetMovieRect() is not { } rect || rect.Size.X <= 0f)
		{
			return true;
		}

		return rect.Size.X >= GuestScreenWidth * 0.75f;
	}

	/// <summary>The guest's 2D screen space, which its own UI is authored against.</summary>
	private const float GuestScreenWidth = 1280f;

	/// <summary>The decoded frame as a texture a UI quad can sample, or null when not playing.</summary>
	public Texture2D? FrameTexture =>
		_decoder is null || EmbedOrdinal < 0 ? null : _embedded.GetTexture();

	public override void _Process(double delta)
	{
		using var _framePhase = Refii.Core.Engine_FramePhases.Measure("gamevideo");
		// Mirrored here so no site that toggles _view can miss the backdrop.
		_backdrop.Visible = _view.Visible;

		if (HostBridge.TryGetAbiVersion() is null)
		{
			return;
		}

		byte[]? requested = HostBridge.GetRequestedVideo(out uint generation, _lastRequest);
		if (generation != _lastRequest)
		{
			_lastRequest = generation;

			// A request carries either the loaded file or its path; the name wins wherever
			// there is one and the guest's buffer is only the fallback. `Engine_VideoLibrary`
			// indexes `art/videos` and every `.bik` ships loose, so the host can resolve
			// anything the guest can — and a failed `BinkOpen` leaves a null handle the guest
			// derefs twice a frame (`BinkDoFrame` 0x82A41F34, and 0x82A42244).
			string path = HostBridge.GetRequestedVideoName(out _);
			if (path.Length != 0 && ResolveMovie(path) is { } byName)
			{
				requested = byName;
			}

			StartVideo(requested);
		}

		AdvanceVideo(delta);

		// The host of an embedded movie is gone — the sprite no longer states a movie, or its
		// presentation is down — so the decoder goes with it; a fullscreen movie ends on its own.
		bool embedded = EmbedOrdinal >= 0;
		if (_wasEmbedded && !embedded && _decoder is not null && !StatedFullscreen())
		{
			GD.Print("[game-video] the element hosting the movie is gone — stopped");
			StopVideo();
		}

		_wasEmbedded = embedded;
	}

	private bool _wasEmbedded;

	/// <summary>
	/// PLAYS A MOVIE THE HOST CHOSE, on the element the host names.
	///
	/// The abilities page needs this because the guest cannot answer for it any more. Its slot-3
	/// tick only swaps the `.bik` on a frame where the packed row hash at `screen+624` changed,
	/// and that hash is the guest's OWN highlight — which stops moving the moment Godot owns the
	/// cursor. So the guest picked exactly one movie, the first row's, and every other ability
	/// kept showing it.
	///
	/// Naming the file is the page's job (`Engine_UiAbilityPage.MovieFor`); opening it goes
	/// through the same library that resolves a movie the guest names, so there is one reader.
	/// Re-asking with the same path is free — the decoder is only replaced when the name changes.
	/// </summary>
	public void PlayEmbedded(string path, int ordinal)
	{
		if (path.Length == 0)
		{
			return;
		}

		// The SAME ask never reopens — playing OR already played. The original plays a
		// preview once per showing and hands the still back; a decoder-gated latch reopened
		// the film every time it ended, which looped it. A row change is a different path
		// and opens fresh.
		if (string.Equals(path, _hostMovie, System.StringComparison.OrdinalIgnoreCase))
		{
			EmbedOrdinal = ordinal;
			return;
		}

		if (ResolveMovie(path) is not { } bytes)
		{
			// Remembered anyway: a name that is not in the library will not be there next frame
			// either, and retrying it every frame would log the same warning at frame rate.
			_hostMovie = path;
			return;
		}

		_hostMovie = path;
		EmbedOrdinal = ordinal;
		StartVideo(bytes);
		_view.Visible = false;
	}

	/// <summary>Drops the host's movie when the page that asked for it is gone.</summary>
	public void StopEmbedded()
	{
		if (_hostMovie.Length == 0)
		{
			return;
		}

		_hostMovie = string.Empty;
		EmbedOrdinal = -1;
		StopVideo();
	}

	/// <summary>
	/// DETACH the host preview without disposing anything: the latch is cleared so a return to
	/// the screen replays it, and the ordinal is dropped so GameVideo's own _Process notices
	/// (`EmbedOrdinal < 0`) and runs the single, owning `StopVideo`. GameUi calls this instead
	/// of `StopEmbedded` so the decoder is freed in exactly one place — see the heap-corruption
	/// note in GameUi._Process.
	/// </summary>
	public void DetachHostMovie()
	{
		_hostMovie = string.Empty;
		EmbedOrdinal = -1;
	}

	/// <summary>Which movie the host last asked for, so an unchanged ask costs no reopen.</summary>
	private string _hostMovie = string.Empty;

	/// <summary>True while the HOST's own embedded ask stands — playing, or finished and
	/// latched on its still. What the orphan teardown in GameUi tests (so it never touches a
	/// guest-named movie), and what clears the latch on leaving the screen so a return
	/// showing plays the preview once again.</summary>
	public bool HasHostMovie => _hostMovie.Length != 0;

	/// <summary>
	/// Reads a movie the guest named rather than loaded, through the same library the editor
	/// uses. The guest path is a console one ("game:\data\art\videos\x.bik"); the library keys
	/// on the file name, so neither the device prefix nor the separators are translated.
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
	/// Plays the .bik the guest just opened, from the buffer it handed BinkOpen.
	/// </summary>
	private void StartVideo(byte[]? requested)
	{
		// A movie the guest replaced before we finished showing it. `FinishMovie` is deliberately
		// NOT called: the open has already republished `g_bink_player`, so it would retire the
		// movie starting rather than the one being cut. Said out loud because it is otherwise
		// invisible — one run played three movies and logged one finish, and two of those were
		// this, not a movie left running.
		if (_decoder is not null)
		{
			GD.Print($"[game-video] movie {MoviesRequested} was cut short — the guest opened " +
				"another before this one reached its last frame");
		}

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

		// Drain the clock, do not consume one frame from it: decoding one frame per host frame
		// makes the movie play slower than real time whenever a host frame outlasts a movie
		// frame, and the guest gates its startup sequence on this clock. Decode until caught
		// up and upload only the frame that lands on screen; intermediate frames are dropped.
		int chromaWidth = 0;
		int chromaHeight = 0;
		int decoded = 0;
		bool ended = false;

		// The guest decides skippability itself (its player holds a threshold and an elapsed
		// accumulator; its skip sites set the flag) and Godot feeds the real button through
		// XInput, so the host only has to notice: a movie the guest retired would otherwise
		// keep playing. REFII_VIDEO_FOLLOW_SKIP=0 ends movies on our decode alone.
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

		// A pause past the catch-up cap must not leave debt that plays out as a fast-forward;
		// dropping the remainder resynchronises to now, where the guest already is.
		if (_frameClock >= _decoder.FrameSeconds)
		{
			_frameClock = 0.0;
		}

		if (ended && HasHostMovie)
		{
			// THE HOST'S PREVIEW PLAYS ONCE and hands the still back — the original's own
			// behaviour on the abilities page. `_hostMovie` stays latched so the per-frame
			// ask does not reopen it; a row change names a different path and does.
			// `FinishMovie` is deliberately NOT sent: there is no guest player to retire and
			// the startup bookkeeping is not this movie's.
			_decoder.Dispose();
			_decoder = null;
			_view.Visible = false;
			GD.Print("[game-video] embedded preview finished — the still stands");
			return;
		}

		if (ended && EmbedOrdinal >= 0)
		{
			// AN EMBEDDED MOVIE LOOPS. The UI sprite's movie item never reads the done byte
			// (`B+221` has no reader in the UI path) and Bink wraps at the last frame, so the
			// ability preview plays until the item is destroyed — a selection change or leaving
			// the screen, both of which arrive here as the host losing the movie.
			//
			// ONLY AN EMBEDDED ONE. This used to loop on `!StatedFullscreen()` as well, as an
			// early stand-in for "embedded" — the rect is known on the first frame while
			// `EmbedOrdinal` is only sampled a few times a second. It is not the same statement,
			// and the failure was not symmetric: a FULLSCREEN movie that took this branch never
			// ended at all. It never called `FinishMovie`, so the guest's player stayed live, and
			// it kept decoding over the title and the menu. Three of six runs left a startup logo
			// unfinished that way.
			//
			// And the rect could not have said "embedded" for a startup logo anyway. The queue's
			// constructor `sub_82389040` states `{left 0, top 0, right BackBufferWidth, bottom
			// BackBufferHeight}` unconditionally at 0x82389148 — the full backbuffer, with no
			// widescreen branch — so a sub-width rect for one of these is never the guest
			// speaking. It is a rect read before the guest wrote it; see StatedFullscreen.
			//
			// Dropping the stand-in costs an embedded movie that reaches its last frame before
			// the follower has named its element: it stops instead of wrapping. That window is
			// well under a second against previews several seconds long, and the wrong answer
			// there is a preview that does not loop rather than a screen that will not clear.
			ReportLoop();

			_decoder.Rewind();
			_frameClock = 0.0;
			return;
		}

		if (ended)
		{
			// End of the movie: stop drawing rather than freezing on the last frame.
			_decoder.Dispose();
			_decoder = null;
			_view.Visible = false;
			++MoviesFinished;

			// Tell the guest, or it never advances: its startup sequence blocks until every
			// movie has played, and it would otherwise decide that from its own Bink decode.
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
		// Either statement keeps it off: the guest's rect, or the UI's hosting ordinal.
		_embeddedView.Texture = _luma;
		_view.Visible = EmbedOrdinal < 0 && StatedFullscreen();

		// Deterministic capture: keyed on frame count, not a wall-clock moment.
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

	/// <summary>
	/// Says WHY a movie went round again, the first time each movie does it — the rect the
	/// guest stated, whether a UI element is hosting it, and the guest's own playback state.
	/// One line, on the frame the decision is made, so a looping startup logo is not something
	/// that has to be inferred from a missing "finished".
	/// </summary>
	private void ReportLoop()
	{
		if (_reportedLoop == MoviesRequested)
		{
			return;
		}

		_reportedLoop = MoviesRequested;
		Rect2? rect = HostBridge.GetMovieRect();
		GD.Print($"[game-video] movie {MoviesRequested} reached its last frame and LOOPED — " +
			$"embedOrdinal {EmbedOrdinal}, " +
			(rect is { } stated
				? $"stated rect {stated.Position.X},{stated.Position.Y} " +
				  $"{stated.Size.X}x{stated.Size.Y} (fullscreen needs width >= " +
				  $"{GuestScreenWidth * 0.75f})"
				: "NO STATED RECT (no live guest player)") +
			$", guest says {HostBridge.MovieState()}");
	}

	private int _reportedLoop = -1;

	private void SaveShot(string path)
	{
		Image image = GetViewport().GetTexture().GetImage();
		System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
		GD.Print($"[game-video] frame shot -> {path} ({image.SavePng(path)})");
	}

	private void Allocate(Engine_BinkDecoder decoder)
	{
		// Buffers sized exactly to their planes: Image.SetData requires the byte count to match
		// the image's dimensions, and an over-sized chroma buffer is rejected silently.
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
