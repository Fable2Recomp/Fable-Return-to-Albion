using Godot;

using Refii.Host;

namespace Refii.Titles.Fable2;

using Refii.Game;

/// <summary>
/// Draws Fable 2's loading screens, in Godot, from the picture the guest picked.
///
/// WHY THIS IS NOT A PRESENTATION, and why it therefore needs a class of its own. Every other
/// screen in the game is Gameface — an element tree the presentation host stands up. The
/// loading screen is not: `sub_8235F108` submits direct textured quads, so there is no tree to
/// mirror and no elements to key state by. What the guest states instead is a texture NAME and
/// a fade, and that pair is the whole interface.
///
/// It has been stating them all along. Every level load in the logs has carried
/// `[guest-loadscreen] … art='Art\GUI\loadingscreens\loading_Bowerstone_Market.tex' fade=0.93`
/// while nothing was drawn — the value was read, printed, and thrown away. So this is not new
/// information about the guest; it is the consumer that was missing.
///
/// THE GUEST DECIDES WHETHER ONE IS UP, not us. `Screen != 0` is the guest wanting a loading
/// screen — the same test its own take-down handshake makes — and `Fade` is how far in or out
/// it is. Neither is inferred from level-change state on our side: a host-side guess about when
/// loading starts and stops is exactly the kind of parallel state machine that ends up
/// disagreeing with the guest at the one moment it matters.
///
/// WHERE IT SITS IN THE STACK is the other half of what this fixes. The layers are, bottom to
/// top: the world (the window's own camera), the UI at <see cref="GameUi"/>'s layer 5, THIS at
/// <see cref="LayerIndex"/>, and the videos at 10. A loading screen must cover the HUD and the
/// front end — the game is between two worlds and neither should show through — but must not
/// cover a movie, because the boot sequence plays movies while levels stream behind them.
/// </summary>
public partial class GameLoadingScreen : Node
{
	/// <summary>
	/// Above the UI at 5, below the videos at 10.
	///
	/// Not adjacent to either by accident: the gap leaves room for anything that has to sit
	/// between the HUD and a movie without renumbering the two ends.
	/// </summary>
	public const int LayerIndex = 8;

	/// <summary>
	/// Where the guest keeps its loading art, and the extension it omits.
	///
	/// The name arrives as a guest path — <c>Art\GUI\loadingscreens\loading_Bowerstone_Market.tex</c>
	/// — with backslashes that mean nothing here and a case the bank index does not share.
	/// </summary>
	private const string TexturePrefix = "game://";

	private CanvasLayer? _layer;


	/// <summary>
	/// The virtual canvas every loading-screen coordinate in the original is expressed in.
	///
	/// `sub_8235E1E0` maps it to the backbuffer: on a wide display the viewport is the whole
	/// screen, so the canvas scales to fill; on 4:3 it is pillarboxed to a centred 4:3 region.
	/// Every rect below is in these units.
	/// </summary>
	private const float CanvasWidth = 1280f;

	private const float CanvasHeight = 720f;

	/// <summary>Where quad A ends and quad B begins, in canvas units. `unk_833197AC`/`unk_833197DC`.</summary>
	private const float SplitX = 1000f;

	/// <summary>
	/// Quad A's atlas rectangle as FRACTIONS of the texture, from `unk_833197BC`.
	///
	/// Held as fractions rather than texels because the texture's real dimensions are not stated
	/// anywhere in the binary — the constants imply 1024x1024, but that is inference. Scaling
	/// the fractions by whatever the loaded texture actually measures is correct either way.
	/// </summary>
	private const float MainU = 0.9766f;

	private const float MainV = 0.7031f;

	/// <summary>Quad B's atlas rectangle, from `unk_833197EC`. `u` spans the strip's 720 rows.</summary>
	private const float StripU = 0.7031f;

	private const float StripVMin = 0.7266f;

	private const float StripVMax = 1.0f;

	/// <summary>
	/// Which of the guest's three art layouts is in force, re-read whenever the art changes.
	///
	/// Null means the guest has no screen object to ask, in which case the two-piece
	/// reassembly is assumed — it is the guest's own fall-through case.
	/// </summary>
	private Fable2Bridge.LoadingLayout? _layout;

	/// <summary>The game's font library, so the tip and banner draw in the title's letters.</summary>
	public Refii.Ui.Engine_UiFontLibrary? Fonts { get; set; }

	/// <summary>
	/// The string database. THE TIP ARRIVES AS A BABEL KEY, not as text — the guest keeps a list
	/// of tag hashes for the group `GUI_SCREEN_LOADING_TIPS` and localises on the fly — so
	/// without this the loading screen has nothing to say.
	/// </summary>
	public Refii.Text.Engine_BabelDatabase? Strings { get; set; }

	/// <summary>The tip and banner layer, drawn over the art.</summary>
	private LoadingFurniture? _furniture;

	/// <summary>
	/// Point size the loading screen's text is drawn at.
	///
	/// THE LOADING SCREEN DOES NOT ASK FOR A SIZE AT ALL. `sub_82360968` passes
	/// `dword_8349803C`, which `sub_83251590` fills with the hash of
	/// `language\Fonts\GUI_Normal.bft` — a font FILE, named outright, with the size coming from
	/// the .bft's own metrics. It is the one screen in the game that skips the point-size bucket
	/// rule every Gameface element goes through.
	///
	/// 18 is how that is said in this project's vocabulary: <c>SelectFor(18)</c> is the single
	/// input that returns GUI_Normal at scale 1.0 exactly, so this reaches the same file at its
	/// authored size while still going through the one font library. Any other value would
	/// either pick a different .bft or apply a residual scale the loading screen never asks for.
	/// </summary>
	private const int TextPointSize = 18;

	/// <summary>The guest's live tip/banner state, re-read every frame.</summary>
	private Fable2Bridge.LoadingText? _text;

	/// <summary>The 4:3 centre crop the fixed-art path uses. `unk_8331979C` = (160,0,1120,720).</summary>
	private const float NarrowInset = 160f;

	/// <summary>Tip text anchor and wrap box, from `sub_82360968`'s arguments.</summary>
	private const float TipX = 358f;

	private const float TipY = 582f;

	private const float TipBoxWidth = 690f;

	/// <summary>The "Loading &lt;level&gt;" banner: centred on the canvas at y 72.</summary>
	private const float BannerCentreX = 640f;

	private const float BannerY = 72f;

	/// <summary>
	/// How far from square a texture may be and still be the folded two-piece packing.
	///
	/// Generous on purpose: the test only has to separate a square-ish sheet from something at
	/// or near the screen's 1.78, and those are nowhere near each other.
	/// </summary>
	private const float SquareTolerance = 0.2f;

	/// <summary>
	/// The painting the guest picked, resolved when its NAME changes and never re-resolved.
	///
	/// ITS SIZE IS DELIBERATELY NOT CACHED. The texture pipeline enhances in the background and
	/// swaps the result in — and reverts it again — under the same asset name, so the same
	/// Texture2D can be 1024 on one frame and 2048 on the next. Every rectangle taken out of it
	/// is therefore computed from <c>GetWidth()</c>/<c>GetHeight()</c> AT DRAW TIME rather than
	/// stored.
	///
	/// This replaced a pair of AtlasTextures whose regions were fixed in TEXELS when the art was
	/// first laid out, plus a per-frame watch that re-split them whenever the dimensions moved.
	/// The watch fired — the logs show it — and the picture was still wrong, because a swap that
	/// lands between the split and the draw leaves a region addressing a quarter of the sheet
	/// with nothing left to notice it by. Measuring at the point of use removes the race instead
	/// of racing it: there is no window in which a stale region can exist.
	/// </summary>
	private Texture2D? _source;

	/// <summary>The last game frame before this screen came up — a fixed-art screen's picture.</summary>
	private Texture2D? _frameAtOpen;

	/// <summary>Opaque black behind the art, so fitting it whole shows nothing through.</summary>
	private ColorRect? _backdrop;

	/// <summary>
	/// Resolves a guest texture path to a texture. Supplied by the owner, because the loader
	/// belongs to the presentation host that mounted the banks — a second cache here would be
	/// the second implementation the engine rules forbid.
	/// </summary>
	public System.Func<string, Texture2D?>? Resolver { get; set; }

	/// <summary>The art currently loaded, so an unchanged name is not re-resolved per frame.</summary>
	private string _loaded = string.Empty;

	/// <summary>True while a loading screen is on screen, for a caller that wants to know.</summary>
	public bool IsShowing
	{
		get => _isShowing;
		private set => _isShowing = Showing = value;
	}

	private bool _isShowing;

	/// <summary>The same, for a caller without this node (there is one loading screen).</summary>
	public static bool Showing { get; private set; }

	public override void _ExitTree() => Showing = false;

	/// <summary>
	/// True while the screen up is a level load's: the painted sheet, or our cover over a world
	/// build. Not a save's "Saving content" (fixed art), which comes down in a few seconds, nor a
	/// screen whose layout has not answered yet.
	/// </summary>
	public bool IsLevelLoad => IsShowing && (_layout is { IsSplit: true } || GameWorld.CoverHeld);

	/// <summary>What the last poll saw, for the log line.</summary>
	private string _reported = string.Empty;

	public override void _Ready()
	{
		_layer = new CanvasLayer { Name = "LoadingLayer", Layer = LayerIndex };
		AddChild(_layer);

		// AN OPAQUE BACKDROP, BEHIND THE ART. It is what lets the art be shown whole.
		//
		// This used to be KeepAspectCovered, whose comment argued that letterboxing would show
		// the world or the HUD through the bars — true, and the one thing a loading screen exists
		// to prevent. But covering solves that by CROPPING: the art is scaled until it fills the
		// window and whatever does not fit is cut off, so on any window that is not exactly the
		// painting's aspect the player never sees the edges of it. Filling the bars with black
		// removes the reason to crop at all.
		//
		// Added first so it draws underneath, and sized by anchors so it tracks a resize.
		_backdrop = new ColorRect
		{
			Name = "LoadingBackdrop",
			Color = Colors.Black,
			MouseFilter = Control.MouseFilterEnum.Ignore,
			Visible = false,
		};
		_backdrop.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		_layer.AddChild(_backdrop);

		// THE WHOLE LOADING SCREEN, painting included, in one drawing node.
		//
		// The art used to live in two TextureRects above this backdrop and below this node. It
		// does not any more, and the reason is worth keeping: a TextureRect needs its region
		// fixed in TEXELS ahead of time, and the texture pipeline changes the sheet's size under
		// the same asset name. Drawing instead lets every rectangle be derived from the sheet's
		// CURRENT dimensions on the frame it is used, which is the only arrangement in which a
		// background swap cannot leave something addressing a quarter of the picture. It also
		// gets the z order right for free — the pieces come out in the original's own order
		// rather than in node order.
		_furniture = new LoadingFurniture
		{
			Name = "LoadingFurniture",
			Owner2 = this,
			MouseFilter = Control.MouseFilterEnum.Ignore,
			Visible = false,
		};
		_furniture.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		_layer.AddChild(_furniture);
	}

	/// <summary>
	/// One frame of the loading screen. Call from the owner's <c>_Process</c>.
	///
	/// POLLED RATHER THAN EVENT-DRIVEN because the fade is a per-frame value: the guest's own
	/// render thread ticks it, and the game thread's one unbounded wait sits on it reaching its
	/// end. Sampling it on a slower schedule would show a loading screen snapping rather than
	/// fading, and would risk missing the frame it finishes on.
	/// </summary>
	public void Poll()
	{
		if (_furniture is null)
		{
			return;
		}

		// THE FIRST BRIDGE READ OF THE FRAME, and it used to be an unguarded one.
		//
		// `GameUi._Process` calls this before any of its own gates, deliberately — a loading
		// screen has to work in exactly the windows the other gates return early on. That put it
		// ahead of every "is the guest usable" test in the app, so a session that had already
		// abandoned its boot still reached the bridge here, which is how an ABI mismatch became a
		// SIGSEGV in `RefiiHostGetLoadingScreen` with the explanation thirty log lines earlier.
		//
		// Hidden rather than merely skipped: leaving the furniture up would freeze whatever
		// loading screen was last drawn over a game that has stopped.
		if (HostBridge.TryGetAbiVersion() is null)
		{
			Hide("[game-loading] the bridge is not usable");
			return;
		}

		if (HostBridge.GetLoadingScreen() is not { } loading || loading.Screen == 0)
		{
			if (GameWorld.CoverHeld)
			{
				HoldCover("[game-loading] the guest dropped its loading screen while the world " +
					"build holds its level change or classic settles its shaders — covering until both release it");
				return;
			}

			Hide("[game-loading] the guest has no loading screen up");
			return;
		}

		if (HostBridge.GetLoadingScreenArt() is not { } art || art.Name.Length == 0)
		{
			if (GameWorld.CoverHeld)
			{
				HoldCover("[game-loading] the guest wants a loading screen but its art is " +
					"unreadable — covering while the world build holds");
				return;
			}

			// A screen is WANTED but its art is unreadable. Reported rather than passed over:
			// the two states want different fixes, and a blank hold looks identical to a
			// loading screen nobody asked for.
			Hide("[game-loading] the guest wants a loading screen but its art is unreadable");
			return;
		}

		if (!string.Equals(art.Name, _loaded, System.StringComparison.OrdinalIgnoreCase))
		{
			_loaded = art.Name;
			_layout = Fable2Bridge.GetLoadingScreenLayout();
			// Before anything of this screen is drawn: the layout can settle frames later, and a
			// capture taken then would be of the loading screen itself.
			_frameAtOpen = CaptureFrame();

			// RESOLVED ONCE PER NAME, MEASURED EVERY FRAME. The lookup is the expensive half
			// and the name changes a handful of times a session; the SIZE is the half that moves
			// under us, and it is read at draw time instead — see the drawing code.
			_source = PickSource(art.Name, _layout);

			Report(_source is null
				? $"[game-loading] the guest picked '{art.Name}' and it did not resolve — " +
				  "holding a black screen rather than showing the world through"
				: $"[game-loading] showing '{art.Name}' (type {art.Type}), " +
				  $"source {_source.GetWidth()}x{_source.GetHeight()}, guest layout " +
				  $"{(_layout is { } l ? l.IsSplit ? "two-piece" : l.FixedArt ? "fixed-art" : "quit-fade" : "unknown")}");
		}

		// THE LAYOUT PROBE RETRIES UNTIL IT ANSWERS, because sampling it once samples it at the
		// worst possible moment.
		//
		// It used to be read only on the frame the art name changed — which is the FIRST frame
		// the screen object exists, when the guest is still standing it up. Both of the last two
		// runs logged `guest layout unknown` for `loading_Bowerstone_Market.tex` and then held
		// that null for the whole load, because a one-shot probe that misses has no second
		// chance. The tip/spinner read goes through the same pointer chain every frame and
		// succeeds, which is what proves the chain itself is fine and the TIMING was the fault.
		//
		// Three byte reads, so retrying costs nothing; it stops the moment it answers.
		if (_layout is null && Fable2Bridge.GetLoadingScreenLayout() is { } settled)
		{
			_layout = settled;
			if (!settled.IsSplit)
			{
				_source = PickSource(_loaded, settled);
			}

			Report($"[game-loading] guest layout for '{_loaded}' settled late: " +
				$"{(settled.IsSplit ? "two-piece" : settled.FixedArt ? "fixed-art" : "quit-fade")}" +
				(settled.Narrow ? ", 4:3 inset" : string.Empty));
		}

		// THE GUEST'S OWN FADE, applied as alpha. It runs 0..1 on the way in and back down on
		// the way out, and following it is what makes the hand-over match the guest's rather
		// than cutting a frame early or late.
		float fade = Mathf.Clamp(loading.Fade, 0f, 1f);

		// THE BACKDROP FADES WITH THE ART. It is part of the loading screen, not a curtain over
		// the game: holding it opaque while the art faded out would leave a black frame after the
		// painting had gone, which reads as a hitch at exactly the moment the world appears.
		if (_backdrop is not null)
		{
			_backdrop.Color = new Color(0f, 0f, 0f, fade);
			_backdrop.Visible = true;
		}

		// THE TIP, THE BANNER AND THE SPINNER. Read every frame: the tip rotates on the guest's
		// own ten-second timer and the spinner's frame counter changes continuously, so a cached
		// read would freeze both.
		_text = Fable2Bridge.GetLoadingScreenText();

		// WHY THE TIP IS OR IS NOT ON SCREEN, stated rather than inferred.
		//
		// "The message of the day is missing" has at least five distinct causes — the bridge
		// read failing, the guest disabling tips, an empty tag list, a tag the string database
		// does not hold, and no font at the point size the screen draws at — and every one of
		// them looks identical from the outside: blank space where the tip should be. Nothing in
		// the logs distinguished them, so this says which. Reported through the change-filter,
		// so a per-frame poll prints it once per state.
		ReportTipState();

		_furniture.Modulate = new Color(1f, 1f, 1f, fade);
		_furniture.Visible = true;
		_furniture.QueueRedraw();

		IsShowing = true;
	}

	/// <summary>
	/// The tip, the "Loading &lt;level&gt;" banner and the spinner, drawn over the art.
	///
	/// A drawing Control rather than more TextureRects because two of the three are glyph runs
	/// and the third is a cell out of a flipbook atlas — none of them is a rect with a whole
	/// image on it. Every coordinate is the original's, in its virtual 1280x720 canvas, scaled
	/// to the window here.
	/// </summary>
	/// <summary>
	/// Maps the original's 1280x720 canvas onto the window: one UNIFORM scale, centred, filling
	/// the window and cropping whatever does not fit.
	///
	/// THE CANVAS IS THE BACKBUFFER IN THE ORIGINAL — there is no scale factor anywhere in
	/// `sub_8235E1E0`. It writes a canvas WINDOW rect and two field-of-view angles, and
	/// `sub_82A3F878` turns a canvas point into NDC with
	/// <c>ndc_x = (x - cx) / (w/2)</c>, <c>ndc_y = (cy - y) / (h/2)</c> against that window. In
	/// widescreen the window is `(0, 0, BackBufferWidth, BackBufferHeight)`, so a canvas pixel
	/// IS a device pixel and the transform is the identity. The proof is in the same file: the
	/// vignette submits its corners in raw backbuffer pixels read straight out of
	/// `qword_834A5FE8` through the same transform as the 1000x720 art quad. One space, no
	/// conversion.
	///
	/// So how a 1280x720 design reaches a modern window is OUR decision — and the original
	/// states the intent in the one case where it does have to choose. On a 4:3 display
	/// `sub_8235E1E0` sets the window to `(160, 0, 1120, 720)`: canvas x=160 becomes NDC -1, so
	/// the middle 960 columns FILL the screen and the outer 160 on each side are cropped away.
	/// It is a crop, not a pillarbox — there are no bars anywhere in the loading screen's code.
	///
	/// <c>max(W/1280, H/720)</c>, centred, is that rule generalised, and it reproduces it
	/// exactly rather than merely resembling it: at 960x720, <c>max(0.75, 1.0) = 1.0</c> and the
	/// x offset is <c>(960 - 1280)/2 = -160</c>, which places canvas x=160 at screen x=0 — the
	/// shipped window, derived rather than special-cased. At any 16:9 size it is a plain
	/// uniform upscale with nothing cropped.
	///
	/// This replaced an independent <c>sx = W/1280</c>, <c>sy = H/720</c>. Those agree with this
	/// at 16:9 and STRETCH at every other aspect, which the original never does.
	/// </summary>
	internal static (float Scale, Vector2 Offset) CanvasToWindow(Vector2 viewport)
	{
		float scale = Mathf.Max(viewport.X / CanvasWidth, viewport.Y / CanvasHeight);
		return (scale, new Vector2(
			(viewport.X - CanvasWidth * scale) * 0.5f,
			(viewport.Y - CanvasHeight * scale) * 0.5f));
	}

	/// <summary>
	/// The canvas fitted by HEIGHT and centred: identical to <see cref="CanvasToWindow"/> up to
	/// 16:9, and uncropped vertically beyond it.
	/// </summary>
	internal static (float Scale, Vector2 Offset) FurnitureToWindow(Vector2 viewport)
	{
		float scale = viewport.Y / CanvasHeight;
		return (scale, new Vector2((viewport.X - CanvasWidth * scale) * 0.5f, 0f));
	}

	private sealed partial class LoadingFurniture : Control
	{
		/// <summary>The screen that owns the state this draws. Named to avoid Node.Owner.</summary>
		public GameLoadingScreen? Owner2 { get; set; }

		/// <summary>Spinner atlas: a 5x5 grid of 100x100 cells. `sub_823601F8`.</summary>
		private const int SpinnerColumns = 5;

		private const int SpinnerFrames = 25;

		private const float SpinnerCell = 100f;

		/// <summary>
		/// The sheet size the 100-unit cell was authored against. Dumping the asset measured it
		/// at 512x512, which is five 100-texel columns with 12 texels left over — the cells do
		/// not tile the sheet exactly, so the grid cannot be derived from the size and has to be
		/// stated.
		/// </summary>
		private const float SpinnerSheetSize = 512f;

		/// <summary>Where the spinner is drawn. `unk_833198DC` = (245, 532, 345, 632).</summary>
		private const float SpinnerX = 245f;

		private const float SpinnerY = 532f;

		/// <summary>
		/// The sheet the panel, the badge and the banner plate are all cut from.
		/// `dword_83497668`, set by `sub_832493F8`.
		/// </summary>
		private const string FrameworkTexture =
			@"art\gui\loadingscreens\Loading_Framework_V2.tex";

		/// <summary>The vignette, one 256-unit square in each corner. `sub_8235FDC0`.</summary>
		private const string VignetteTexture = @"art\gui\frames\vignette256.tex";

		private const float VignetteSize = 256f;

		/// <summary>
		/// One drawn piece: where it goes in canvas units, and which part of the sheet it takes.
		///
		/// THE UVs ARE ALREADY FRACTIONS in the original — `sub_8235ED50` is handed an eight-float
		/// block of four (u,v) pairs, not a texel rect — so these survive the upscaler with no
		/// adjustment at all. That is the difference between this and the spinner, whose cell grid
		/// the guest computes by multiplying 100 by `flt_82101028 = 1/512` and which therefore has
		/// the sheet's authored size baked into it.
		/// </summary>
		private readonly record struct Piece(Rect2 Canvas, Rect2 Uv);

		/// <summary>
		/// The tip panel, in two halves that meet at x 640. `unk_8331981C`/`unk_8331982C` for the
		/// destinations, `unk_8331987C`/`unk_8331989C` for the UVs.
		///
		/// The two halves are NOT the same slice mirrored — the right one is shifted a texel
		/// across and sits on a different band of the sheet — so they are stated separately.
		/// </summary>
		private static readonly Piece TipPanelLeft = new(
			new Rect2(64f, 502f, 576f, 160f),
			new Rect2(0f, 0.01953125f, 0.5625f, 0.3125f));

		private static readonly Piece TipPanelRight = new(
			new Rect2(640f, 502f, 576f, 160f),
			new Rect2(0.0009765625f, 0.328125f, 0.5634765625f, 0.3125f));

		/// <summary>The badge the spinner turns inside. `unk_8331983C` / `unk_833198BC`.</summary>
		private static readonly Piece SpinnerBadge = new(
			new Rect2(223f, 510f, 144f, 144f),
			new Rect2(0.6171875f, 0.109375f, 0.140625f, 0.28125f));

		/// <summary>
		/// The plate behind the banner. Its width is not authored — `sub_82360618` measures the
		/// text and builds the rect around it — so only the UVs and the two y edges are constant.
		/// `unk_8331985C`, `flt_83319810` = 60, `flt_83319818` = 110.
		/// </summary>
		private static readonly Rect2 BannerPlateUv =
			new(0.015625f, 0.673828125f, 0.7578125f, 0.187500f);

		private const float BannerPlateTop = 60f;

		private const float BannerPlateBottom = 110f;

		/// <summary>Half the horizontal padding the plate adds around the measured text.</summary>
		private const float BannerPlatePadding = 30f;

		public override void _Draw()
		{
			if (Owner2 is not { } owner || owner._text is not { } state)
			{
				return;
			}

			// THE SAME TRANSFORM THE ART USES. The furniture and the painting are one picture in
			// the original — every rect in `sub_8235E5A0` is submitted through the one canvas
			// window — so any second rule here would slide the tip off its panel the moment the
			// window stopped being 16:9.
			(float scale, Vector2 offset) = CanvasToWindow(GetViewportRect().Size);

			// BACK TO FRONT, in the original's own z order: the pieces carry 10 for the painting,
			// 9 for the vignette, the tip panel and the banner plate, 8 for the badge and 7 for
			// the spinner, so a LARGER z is further back. Draw order here is that list reversed.
			DrawArt(owner, scale, offset);
			DrawVignette(owner, scale);

			// The painting covers; the furniture must stay ON SCREEN. The guest knows only 4:3
			// and 16:9 (`sub_8235E1E0`), where the two rules agree — wider than 16:9, cover
			// crops top and bottom and takes the banner and the tip panel with it.
			(scale, offset) = FurnitureToWindow(GetViewportRect().Size);

			// The tip panel only makes sense under a tip, and the original rebuilds it under the
			// same flag — a panel with nothing on it is furniture the player has to wonder about.
			if (state.TipsEnabled)
			{
				DrawPiece(owner, FrameworkTexture, TipPanelLeft, scale, offset);
				DrawPiece(owner, FrameworkTexture, TipPanelRight, scale, offset);
				DrawPiece(owner, FrameworkTexture, SpinnerBadge, scale, offset);
			}

			DrawSpinner(owner, scale, offset);

			if (owner.Fonts?.For(TextPointSize) is not { } picked)
			{
				return;
			}

			(Refii.Ui.UiFont font, float fontScale) = picked;
			float textScale = fontScale * scale;

			// THE TIP, black — `dword_8331B158` is 00 00 00 FF. An overlay set by the host side
			// of the guest takes precedence, which is what `screen+280` exists for.
			if (state.TipsEnabled)
			{
				string tip = state.Overlay.Length > 0
					? state.Overlay
					: ResolveTip(owner, state.TipKey);

				if (tip.Length > 0)
				{
					// THE ANCHOR IS A VERTICAL CENTRE, NOT A TOP EDGE. `sub_82360968` is called
					// with its horizontal-centre flag SET (so x = 358 is taken literally and the
					// text is left-aligned) and its vertical-centre flag CLEAR, which runs
					// `y = 582 - lineHeight * lineCount * 0.5`.
					//
					// `lineCount` IS THE LAID-OUT LINE COUNT, not the number of '\n' in the
					// source. The engine counts character 10, but it counts them AFTER wrapping
					// has rewritten the string with the breaks it chose — which is why the search
					// exists at all.
					//
					// The panel settles it without needing to read that loop again: the tip panel
					// spans y 502..662, so its centre is exactly 582, exactly the anchor. A tip
					// is meant to sit centred in its panel, and that only comes out right when
					// the half-height subtracted here is the half-height actually drawn. Counting
					// source newlines instead put a three-line tip 30 units low and hanging over
					// the panel's bottom edge, which is what the last screenshot showed.
					int lines = Refii.Ui.Engine_UiCanvasText.MeasureWrappedLines(
						font, tip, TipBoxWidth * scale, textScale);

					float top = TipY * scale - font.Font.LineHeight * textScale * lines * 0.5f;

					Refii.Ui.Engine_UiCanvasText.DrawWrapped(
						this, font, tip, offset + new Vector2(TipX * scale, top),
						TipBoxWidth * scale, textScale, Colors.Black);
				}
			}

			// THE BANNER, white and CENTRED — measured first because the original centres it on
			// the canvas rather than anchoring a corner, and because the PLATE behind it is built
			// around the measurement too.
			if (state.BannerEnabled && state.LevelName.Length > 0)
			{
				string banner = Localise(owner, "GUI_LOADING_SCREEN_LOADING_LEVEL",
					owner.LevelDisplayName(state.LevelName));
				float width = Refii.Ui.Engine_UiCanvasText.Measure(font, banner) * fontScale;

				// `x0 = 640 - w/2 - 30`, `x1 = 640 - w/2 + w + 30` — so the plate is always
				// `w + 60` wide and, for an odd width, reaches half a unit further right than
				// left. Written the original's way rather than as a symmetric inset, because the
				// asymmetry is the shipped arithmetic rather than an artefact of describing it.
				float left = BannerCentreX - width * 0.5f;
				DrawPiece(owner, FrameworkTexture, new Piece(
					new Rect2(left - BannerPlatePadding, BannerPlateTop,
						width + BannerPlatePadding * 2f, BannerPlateBottom - BannerPlateTop),
					BannerPlateUv), scale, offset);

				Refii.Ui.Engine_UiCanvasText.DrawLine(
					this, font, banner,
					offset + new Vector2(left * scale, BannerY * scale),
					textScale, Colors.White);
			}
		}

		/// <summary>
		/// The painting itself, in one piece or two.
		///
		/// EVERY REGION IS COMPUTED HERE, FROM THE TEXTURE'S SIZE ON THIS FRAME. That is the
		/// whole point of drawing it rather than parking it in a TextureRect: the UV constants
		/// are fractions, the sheet's dimensions are whatever the upscaler last made them, and
		/// multiplying the two at the moment of use means there is never a stored rectangle to go
		/// stale. Composing the shipped 1024x1024 `loading_Bowerstone_Market.tex` offline against
		/// these exact fractions gives a seamless 1280x720 picture — a misty Bowerstone on the
		/// left running into a map of the region on the right — so the arithmetic below is
		/// checked against the art, not only against the disassembly.
		/// </summary>
		private void DrawArt(GameLoadingScreen owner, float scale, Vector2 offset)
		{
			if (owner._source is not { } art)
			{
				return;
			}

			float width = art.GetWidth();
			float height = art.GetHeight();
			if (width <= 0f || height <= 0f)
			{
				return;
			}

			// ONE QUAD OR TWO — AND THE IMAGE GETS THE CASTING VOTE.
			//
			// The guest's flags say which layout IT would use, which is necessary and not
			// sufficient, because the texture reaching us is not always the one it shipped. The
			// fold is only POSSIBLE in a square sheet — 1280x720 of picture folded to fit
			// 1024x1024 is the entire reason the right-hand 280 columns are stored on their side
			// — so a replacement authored at the screen's aspect cannot be folded and must be
			// drawn whole. A ratio test rather than a size test, because hard-coding 1024 would
			// break on the first upscale.
			bool packed = System.Math.Abs(width / height - 1f) <= SquareTolerance;

			if (owner._layout is { IsSplit: false } || !packed)
			{
				bool narrow = owner._layout is { Narrow: true };
				float left = narrow ? NarrowInset : 0f;
				var single = new Rect2(
					offset + new Vector2(left * scale, 0f),
					new Vector2((CanvasWidth - left * 2f) * scale, CanvasHeight * scale));

				DrawTextureRectRegion(art, single, new Rect2(0f, 0f, width, height));
				return;
			}

			// `unk_833197AC` = (0, 0, 1000, 720) ← u 0..0.9765625, v 0..0.703125, upright.
			DrawTextureRectRegion(art,
				new Rect2(offset, new Vector2(SplitX * scale, CanvasHeight * scale)),
				new Rect2(0f, 0f, MainU * width, MainV * height));

			// `unk_833197DC` = (1000, 0, 1280, 720), stored ROTATED.
			//
			// Its four shipped UV pairs put u 0 along the destination's BOTTOM edge and u max
			// along its top, while v runs left to right — so screen X samples V, screen Y samples
			// U, and U increases UPWARD. Rotating the drawing transform by -90 about the
			// destination's bottom-left corner is that mapping: it sends local (x, y) to (y, -x),
			// which puts local +X up the screen and local +Y across it.
			DrawSetTransform(
				offset + new Vector2(SplitX * scale, CanvasHeight * scale),
				-Mathf.Pi / 2f, Vector2.One);

			DrawTextureRectRegion(art,
				// In the block's own orientation: 720 along local X, 280 along local Y. The
				// rotation is what maps those onto the screen's Y and X.
				new Rect2(0f, 0f, CanvasHeight * scale, (CanvasWidth - SplitX) * scale),
				new Rect2(0f, StripVMin * height, StripU * width,
					(StripVMax - StripVMin) * height));

			DrawSetTransform(Vector2.Zero, 0f, Vector2.One);
		}

		/// <summary>
		/// Draws one sheet-cut piece: canvas rect to the window, UV fractions to texels.
		///
		/// The region is computed against the texture's CURRENT size every call, which is what
		/// makes the whole set upscaler-proof — a 1024x512 sheet becoming 2048x1024 changes the
		/// texels and not the fractions, so nothing here has to know it happened.
		/// </summary>
		private void DrawPiece(GameLoadingScreen owner, string texture, Piece piece,
			float scale, Vector2 offset)
		{
			if (owner.Resolve(texture) is not { } sheet)
			{
				return;
			}

			var source = new Rect2(
				piece.Uv.Position.X * sheet.GetWidth(),
				piece.Uv.Position.Y * sheet.GetHeight(),
				piece.Uv.Size.X * sheet.GetWidth(),
				piece.Uv.Size.Y * sheet.GetHeight());

			var destination = new Rect2(
				offset + piece.Canvas.Position * scale,
				piece.Canvas.Size * scale);

			DrawTextureRectRegion(sheet, destination, source);
		}

		/// <summary>
		/// The four corner vignettes.
		///
		/// ANCHORED TO THE WINDOW, NOT TO THE CANVAS, because that is what the original does:
		/// `sub_8235FDC0` builds these rects from `qword_834A5FE8`, the backbuffer size, while
		/// every other piece on the screen is in canvas units. On the shipped 1280x720 backbuffer
		/// the two spaces coincide, which is why it works there and why it has to be written out
		/// here — our window is any size, and a vignette placed in canvas units would drift off
		/// the corner it exists to darken.
		///
		/// THE MIRRORING DEVIATES, DELIBERATELY. The original rotates a four-slot (bottom-left,
		/// bottom-right, top-left, top-right) UV list by a corner index at `screen+296`. For the
		/// top-left and bottom-left corners that yields the identity and a clean vertical flip;
		/// for the other two it yields a mapping whose left and right edges run v in OPPOSITE
		/// directions, which is not affine and which no quad renderer can express. A radially
		/// symmetric corner darkening is what the art is, so the two remaining corners are the
		/// honest mirrors of the first two.
		/// </summary>
		private void DrawVignette(GameLoadingScreen owner, float scale)
		{
			if (owner.Resolve(VignetteTexture) is not { } corner)
			{
				return;
			}

			Vector2 viewport = GetViewportRect().Size;
			float size = VignetteSize * scale;
			var whole = new Rect2(0f, 0f, corner.GetWidth(), corner.GetHeight());

			// (destination, flip-x, flip-y) per corner.
			(Vector2 At, bool FlipX, bool FlipY)[] corners =
			{
				(Vector2.Zero, false, false),
				(new Vector2(viewport.X - size, 0f), true, false),
				(new Vector2(0f, viewport.Y - size), false, true),
				(new Vector2(viewport.X - size, viewport.Y - size), true, true),
			};

			foreach ((Vector2 at, bool flipX, bool flipY) in corners)
			{
				// Godot mirrors a region by giving its rect a negative extent, which keeps this
				// to one call per corner rather than four transformed draws.
				var source = new Rect2(
					flipX ? whole.Size.X : 0f,
					flipY ? whole.Size.Y : 0f,
					flipX ? -whole.Size.X : whole.Size.X,
					flipY ? -whole.Size.Y : whole.Size.Y);

				DrawTextureRectRegion(corner, new Rect2(at, new Vector2(size, size)), source);
			}
		}

		/// <summary>
		/// The animated spinner — a timed flipbook, NOT load progress.
		///
		/// `sub_823601F8` takes the screen's frame counter modulo 25 and indexes a 5x5 grid of
		/// 100x100 cells. There is no progress bar anywhere in the game, so a host that drew one
		/// would be inventing information it does not have.
		/// </summary>
		private void DrawSpinner(GameLoadingScreen owner, float scale, Vector2 offset)
		{
			if (owner._text is not { } state || owner.Resolve(SpinnerTexture) is not { } atlas)
			{
				return;
			}

			// THE CELL GRID IS MEASURED AGAINST THE SHEET WE ACTUALLY GOT, not against 512.
			//
			// The same trap the painting had, and for the same reason: the texture pipeline
			// upscales in the background and swaps the result in under the same asset name. A
			// 100-texel cell out of a sheet that has become 1024 wide is half a cell, so the
			// spinner would show a quarter of itself and stop moving between the frames that
			// happen to land in the surviving quadrant. `SpinnerCell` is the AUTHORED size;
			// multiplying by the sheet's real width over the authored width keeps it a fraction.
			float cell = SpinnerCell * (atlas.GetWidth() / SpinnerSheetSize);
			int frame = (int)(state.SpinnerFrame % SpinnerFrames);
			var source = new Rect2(
				frame % SpinnerColumns * cell,
				frame / SpinnerColumns * cell,
				cell, cell);

			var destination = new Rect2(
				offset + new Vector2(SpinnerX, SpinnerY) * scale,
				new Vector2(SpinnerCell, SpinnerCell) * scale);

			DrawTextureRectRegion(atlas, destination, source);
		}

		/// <summary>Looks the tip's Babel key up, or returns empty when it is not known.</summary>
		private static string ResolveTip(GameLoadingScreen owner, uint key) =>
			key != 0 && owner.Strings is { } strings &&
			strings.TryGetString(key, out string tip)
				? tip
				: string.Empty;

		/// <summary>
		/// The token the banner's format string reserves for the level name.
		///
		/// `sub_82360618` builds it from `word_820A3118`, whose bytes are
		/// <c>00 25 00 31 00 00</c> — UTF-16 big-endian <c>"%1"</c> — and hands it to
		/// `sub_82B3EB68` as the needle to SUBSTITUTE. It is a placeholder inside the localised
		/// string, not a separator between two of them.
		/// </summary>
		private const string LevelToken = "%1";

		/// <summary>
		/// Builds the banner: the localised format string with the level substituted into it.
		///
		/// THIS USED TO CONCATENATE, and the screenshot said so plainly — "Loading %1 BWSMarket",
		/// with the placeholder still sitting in the middle of the sentence and the name stuck on
		/// the end. `sub_82B3EB68` replaces; it does not append.
		///
		/// A format string with no token still gets the name appended, because a localisation
		/// that has lost its placeholder should degrade to a banner that names the level rather
		/// than to one that never mentions it.
		///
		/// THE LEVEL IS STILL ITS RAW KEY. `screen+268` holds "BWSMarket", not "Bowerstone
		/// Market": the original passes it through `sub_8231CE98`, which hashes it into the live
		/// world structure and only falls back to a text-tag table when that misses. That lookup
		/// is not decoded yet, and picking a shipped table that merely looks right — the region
		/// tags in `fasttravellist.txt` map every cave to its parent region, which would name
		/// them all wrongly — would be guessing. Showing the key is honest until it is.
		/// </summary>
		private static string Localise(GameLoadingScreen owner, string tag, string level)
		{
			if (owner.Strings is not { } strings || !strings.TryGetString(tag, out string format))
			{
				return level;
			}

			return format.Contains(LevelToken)
				? format.Replace(LevelToken, level)
				: format + " " + level;
		}
	}

	/// <summary>The spinner's flipbook. `dword_83497698`.</summary>
	private const string SpinnerTexture = @"art\gui\loadingscreens\Loading_Animation.tex";

	/// <summary>
	/// Prints the whole tip/banner chain in one line, from the guest's flags through to the
	/// resolved string. Uses its own change-filter so it does not compete with the art report.
	/// </summary>
	private void ReportTipState()
	{
		string line;
		if (_text is not { } state)
		{
			line = "[game-loading-text] the bridge returned nothing — no screen object, or the " +
				"loaded host bridge predates RefiiHostGetLoadingScreenText";
		}
		else
		{
			string resolved = state.TipKey == 0
				? "no tag"
				: Strings is null
					? "NO STRING DATABASE"
					: Strings.TryGetString(state.TipKey, out string tip)
						? $"\"{(tip.Length > 60 ? tip[..60] + "…" : tip)}\""
						: "TAG NOT IN book.babel";

			string font = Fonts is null
				? "NO FONT LIBRARY"
				: Fonts.For(TextPointSize) is null ? $"NO FONT AT {TextPointSize}pt" : "font ok";

			line = $"[game-loading-text] tips={(state.TipsEnabled ? "on" : "OFF")} " +
				$"banner={(state.BannerEnabled ? "on" : "OFF")} " +
				$"tip {state.TipIndex + 1}/{state.TipCount} key=0x{state.TipKey:X8} → {resolved}, " +
				$"level='{state.LevelName}', overlay='{state.Overlay}', {font}";
		}

		if (line == _reportedText)
		{
			return;
		}

		_reportedText = line;
		GD.Print(line);
	}

	/// <summary>What the last tip report said, so an unchanged state prints once.</summary>
	private string _reportedText = string.Empty;

	/// <summary>
	/// The level's display name, through <see cref="GameWhereabouts.DisplayName"/>.
	///
	/// `screen+268` holds the level KEY — "BWSMarket", not "Bowerstone Market". The original
	/// resolves it through `sub_8231CE98`, which asks the world's script-override map first and
	/// then the `LevelNameTextTags` record in `globals.gdb`; see
	/// <see cref="Refii.Text.Engine_LevelNames"/> for why neither of the two obvious shipped
	/// text files is that table.
	///
	/// Read through the SAME bank resolver the art goes through rather than off disk, because
	/// `globals.gdb` is a bank entry and this project does not extract game data to read it.
	/// </summary>
	private string LevelDisplayName(string levelKey) =>
		GameWhereabouts.DisplayName(levelKey, Strings);


	/// <summary>
	/// Our cover, not the guest's screen: fully opaque, over whatever art the guest last showed
	/// (black when it never had readable art). Shown while Godot is still building the region.
	/// </summary>
	private void HoldCover(string why)
	{
		if (_backdrop is not null)
		{
			_backdrop.Color = Colors.Black;
			_backdrop.Visible = true;
		}

		if (_furniture is not null)
		{
			_furniture.Modulate = Colors.White;
			_furniture.Visible = _source is not null;
			_furniture.QueueRedraw();
		}

		IsShowing = true;
		Report(why);
	}

	/// <summary>
	/// Takes the screen down and says why, once.
	/// </summary>
	/// <remarks>
	/// The reason arrives already prefixed and constant: `Poll` runs every frame, ahead of every
	/// gate in <c>GameUi</c>, so formatting it here would allocate per frame only for
	/// <see cref="Report"/> to drop the duplicate.
	/// </remarks>
	private void Hide(string why)
	{
		// FORGOTTEN WITH THE SCREEN. Holding it would carry one load's layout into the next,
		// and the retry above only runs while it is null.
		_layout = null;
		_reportedText = string.Empty;

		if (_furniture is not null)
		{
			_furniture.Visible = false;
		}

		if (_backdrop is not null)
		{
			_backdrop.Visible = false;
		}

		IsShowing = false;
		_loaded = string.Empty;
		// The painting goes too, or the next load's cover would show this region's art.
		_source = null;
		_frameAtOpen = null;
		_held.Clear();
		Report(why);
	}

	/// <summary>The quit-without-save fade's art, `sub_82360EC0`'s first choice.</summary>
	private const string QuitFadeTexture = @"Art\GUI\HUD\BackgroundFade.tex";

	/// <summary>
	/// The painting, chosen the way `sub_82360EC0` chooses it: the quit fade (`screen+310`)
	/// draws `BackgroundFade.tex`; a fixed-art screen (`screen+315`, every save) draws
	/// `Art\GUI\Persistant\Screenshot.tex` (`dword_8349AA04`, hashed at 0x8325A478), a texture
	/// the guest renders from the game frame (`sub_82677468`); only a level load draws the
	/// `screen+272` sheet. Drawing that sheet for a save put a folded region map behind
	/// "Saving content". Godot owns the frame, so the fixed art is our own last frame, taken
	/// when the screen first appears.
	/// </summary>
	private Texture2D? PickSource(string guestName, Fable2Bridge.LoadingLayout? layout)
	{
		if (layout is { QuitFade: true })
		{
			return Resolve(QuitFadeTexture);
		}

		if (layout is { FixedArt: true })
		{
			return _frameAtOpen ?? Resolve(guestName);
		}

		return Resolve(guestName);
	}

	private Texture2D? CaptureFrame()
	{
		Image? frame = GetViewport()?.GetTexture()?.GetImage();
		return frame is null || frame.IsEmpty() ? null : ImageTexture.CreateFromImage(frame);
	}

	/// <summary>
	/// Loads the guest's texture name through the owner's resolver.
	///
	/// The name is a GUEST path: backslashes, and a case the bank index does not share. Both
	/// are normalised here rather than at the resolver, because the resolver is the UI's and
	/// every other caller hands it a path that came out of a `.fac` already in that shape.
	/// </summary>
	/// <remarks>
	/// HELD WHILE THE SCREEN IS UP. The resolver's cache is Godot's ResourceLoader, which frees a
	/// texture as soon as nothing references it, and a texture fetched for one <c>_Draw</c> is
	/// referenced only by its C# wrapper until the next collection. A level load collects every
	/// few frames, so the tip panel's sheet was freed and loaded again about twenty times per load
	/// on the S25+, and each time the canvas still drew the freed texture for a frame, which the
	/// renderer draws white: the tip panel flashed. Holding it costs one sheet and also stops the
	/// decode being paid again in the middle of a load.
	/// </remarks>
	private Texture2D? Resolve(string guestPath)
	{
		if (Resolver is null)
		{
			return null;
		}

		if (_held.TryGetValue(guestPath, out Texture2D? held) && IsInstanceValid(held))
		{
			return held;
		}

		string path = guestPath.Replace('\\', '/').ToLowerInvariant();
		Texture2D? texture = Resolver(path) ?? Resolver(TexturePrefix + path);
		if (texture is not null)
		{
			_held[guestPath] = texture;
		}

		return texture;
	}

	/// <summary>The textures this screen has drawn since it came up, held until it goes down.</summary>
	private readonly System.Collections.Generic.Dictionary<string, Texture2D> _held =
		new(System.StringComparer.OrdinalIgnoreCase);

	/// <summary>Prints a line only when it changes, so a per-frame poll does not flood.</summary>
	private void Report(string line)
	{
		if (line == _reported)
		{
			return;
		}

		_reported = line;
		GD.Print(line);
	}
}
