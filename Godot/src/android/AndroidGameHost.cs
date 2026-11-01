using Godot;
using Refii.Assets;
using Refii.Audio;
using Refii.Core;
using Refii.Game;
using Refii.Host;
using Refii.Models;
using Refii.Textures;
using Refii.Ui;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Refii.AndroidRuntime;

/// <summary>Android entry point for the production UI, world, materials and audio, played by controller or touch.</summary>
/// <remarks>Native execution has one persistent owner; Godot presentation stays on its main
/// thread. GameSession is intentionally absent because it would initialize and step a second guest.
/// The APK contains no game data or generated game module. Supply physical paths through the
/// private configuration documented by AndroidRuntimeOptions before starting the app.</remarks>
public partial class AndroidGameHost : Node3D
{
    private AndroidRuntimeOptions? _options;
    private AndroidNativeSession? _guest;
    private AndroidGuestPlacement? _placement;
    private Engine_GameAssets? _assets;
    private GameWorld? _world;
    private GameAudio? _audio;
    private GameInput? _input;
    private Node? _uiNode;
    private IGameTitleUi? _ui;
    private Control? _touchNode;
    private IAndroidTouchControls? _touch;
    private CanvasLayer? _uiLayer;
    private bool? _canvasLeanSetting;
    private Label? _status;
    private object? _frontEnd;
    private bool _assetsReady, _focused = true;

    /// <summary>
    /// Seconds until the return's pause is pressed, once the window has its focus back: resume
    /// arrives before focus, and the pad is muted until focus, so a press then would be lost.
    /// Negative while no return waits.
    /// </summary>
    private double _pauseOnReturn = -1;

    /// <summary>How long the world runs again before the return's pause is pressed.</summary>
    private const double PauseOnReturnDelay = 0.3;
    private string _phase = "waiting for private Android configuration";
    private string _failure = string.Empty;
    private string _userDirectory = string.Empty;
    private string _runtimeConfig = string.Empty;
    private string _renderScaleError = string.Empty;
    private string _renderThreadError = string.Empty;
    private readonly AndroidRenderScale _renderScale = new();
    private readonly Engine_MeshLodPolicy _meshLod = new();
    private AndroidProfilingOptions? _profiling;
    private double _reportAge;
    private long _presentationFrames;

    [DllImport("libc", CallingConvention = CallingConvention.Cdecl)]
    private static extern int setenv(string name, string value, int overwrite);

    /// <summary>Sets writable paths, uncapped presentation and bounded renderer quality before mounting the supplied game.</summary>
    public override void _Ready()
    {
        AndroidNativeLog.StartPump();
        AndroidLastExit.RecordOnce();
        ProcessPriority = -100;
        // The back gesture is the game's (AndroidSystemKeys), never Godot's quit.
        GetTree().QuitOnGoBack = false;
        _userDirectory = OS.GetUserDataDir();
        BuildStatusOverlay();
        _touchNode = GetNodeOrNull<Control>("AndroidTouchLayer/AndroidTouchControls");
        _touch = _touchNode as IAndroidTouchControls;
        if (_touch != null && GetNodeOrNull<AndroidDiagnosticButtons>("AndroidDiagnosticButtons") is { } diagnostics)
        {
            _touch.PassThrough = diagnostics.Rects;
            _touch.OptionsPressed = diagnostics.Toggle;
            diagnostics.ConfigPath = () => _runtimeConfig;
            diagnostics.SettingsWritten = RefreshRenderScale;
        }
        if (_touch != null)
            AddChild(new AndroidCanvasLean { Name = "CanvasLean", Touch = _touchNode, Target = UiLayer });
        try
        {
            string configPath = Path.Combine(_userDirectory, AndroidRuntimeOptions.FileName);
            if (!File.Exists(configPath))
                throw new FileNotFoundException("Supply private android-runtime.json before booting; see docs/android-runtime.md", configPath);
            _options = AndroidRuntimeOptions.Parse(File.ReadAllText(configPath), _userDirectory);
            string root = Path.Combine(_userDirectory, "runtime");
            SetEnvironment(Engine_AppRoot.EnvironmentVariable, root);
            Engine_AppRoot.Invalidate();
            Engine_AppRoot.EnsureLayout();
            SetEnvironment(Engine_GameData.EnvironmentVariable, _options.GameRoot);
            // These are the bounded, original-material settings used by the verified S25+
            // prototype. The production scene-referred HDR/post-processing code stays intact.
            SetEnvironment("REFII_MATERIALS_MODERNIZED", "0");
            SetEnvironment("REFII_TEX_UPSCALE", "0");
            SetEnvironment("REFII_TEX_ORM", "0");
            SetEnvironment("REFII_TEX_WORKERS", "1");
            SetEnvironment("REFII_TEX_CACHE_MB", "512");
            SetEnvironment("REFII_LIGHTING_MODEL", "fable");
            Refii.Lighting.Engine_GiRefresh.ForcedOff = true;
            Engine_TickClock.HostOwnsClock = false;
            Engine_TickClock.UseStatedClock = true;
            RenderingServer.DirectionalShadowAtlasSetSize(1024, true);
            GetViewport().Msaa3D = Viewport.Msaa.Disabled;
            GetViewport().UseTaa = false;
            Engine_RenderStats.Track(GetViewport());
            AndroidTextureUpload.InitializeOnMainThread();
            string runtimeConfig = Path.Combine(root, "refii.toml");
            if (!File.Exists(runtimeConfig))
                File.WriteAllText(runtimeConfig, "sun_shadow_map = 1024\nsun_shadow_splits = 2\nshader_warmup_enabled = false\n");
            var seeded = Refii.Launcher.Core.RuntimeConfig.ReadValues(runtimeConfig);
            if (AndroidLauncherSettings.ApplyPresetOnce(seeded) | Refii.Launcher.Core.GraphicsPresets.Upgrade(seeded))
                Refii.Launcher.Core.RuntimeConfig.WriteValues(runtimeConfig, seeded, out _);
            _runtimeConfig = runtimeConfig;
            // The thread model is fixed at start: this states it for the next one.
            _renderThreadError = AndroidRenderThread.Apply(Refii.Launcher.Core.RuntimeConfig.ReadValues(runtimeConfig));
            Engine_PresentationPolicy.ApplyAndroid(
                Engine_RefiiConfig.Int(Refii.Launcher.Core.RuntimeConfig.ReadValues(runtimeConfig), "max_fps", 0),
                value => Engine.MaxFps = value,
                value => DisplayServer.WindowSetVsyncMode(value, (int)DisplayServer.MainWindowId));
            SetEnvironment(Refii.Lighting.Engine_PostFxParams.ModeEnvironment,
                Engine_PresentationPolicy.PostFxMode(Refii.Launcher.Core.RuntimeConfig.ReadValues(runtimeConfig)));
            ConfigureProfiling();
            // Avoid eager compilation of unused shader families: older Adreno drivers can
            // hang there. This does not replace any authored shader or skip world rendering.
            _assets = Engine_GameAssets.Instance ?? throw new InvalidOperationException("GameAssets autoload is missing");
            _assets.Mounted += OnMounted;
            _assets.MountFailed += OnMountFailed;
            _phase = "mounting privately supplied game assets";
            _assets.Mount(_options.GameRoot);
            Engine_ShaderWarmup.Begin(this);
        }
        catch (Exception error) { Fail(error); }
        SaveStatus();
    }

    /// <summary>Pumps presentation and real controller UI while the owner thread runs the native guest.</summary>
    public override void _Process(double delta)
    {
        PauseOnReturn(delta);
        try
        {
            if (_failure.Length == 0 && _guest == null && _assetsReady && Engine_ShaderWarmup.Done &&
                _assets != null && _options != null)
            {
                // GameAudio is the sole mixed-audio consumer, including during initialization.
                _audio = new GameAudio { Name = "GameAudio" };
                AddChild(_audio);
                _guest = new AndroidNativeSession(_options, Engine_AppRoot.Path, _assets,
                    _runtimeConfig.Length != 0 &&
                    AndroidNativeSession.RealClockConfigured(Refii.Launcher.Core.RuntimeConfig.ReadValues(_runtimeConfig)));
                _guest.Start();
                // The guest's game thread on the fastest cores once it is named (android_guest_prime_core).
                if (_runtimeConfig.Length != 0)
                {
                    var values = Refii.Launcher.Core.RuntimeConfig.ReadValues(_runtimeConfig);
                    _placement = AndroidGuestPlacement.Start(values);
                    AndroidGcStress.Start(values);
                }
                _phase = "booting native guest";
                Engine_ShaderWarmup.ReleaseScreen();
            }
            if (_guest?.Error.Length > 0 && _failure.Length == 0)
                Fail(new InvalidOperationException(_guest.Error));
            if (_failure.Length == 0 && _guest?.IsInitialized == true)
            {
                // Match desktop's early worker kick: UI/world work can overlap the read,
                // then GameEntities takes it in this frame rather than scanning synchronously.
                // Engine_FrameStartNode already sampled this frame's guest frame; sampling it
                // again would compare the frame with itself and clear Changed, starving the
                // entity walk. Like GameWindow, re-read only while no frame has been seen.
                AndroidEntityFeed.SampleAndKick(GameEntities.FeedEveryFrame,
                    static () => { if (Engine_GuestFrame.Frame == 0u) HostBridge.SampleGuestFrame(); },
                    static () => Engine_GuestFrame.Changed, HostEntityWalk.Kick);
                if (HostBridge.SimClock() is { } clock)
                {
                    Engine_TickClock.PublishGuestGameTick(clock.Tick);
                    Engine_TickClock.ObserveCounterPair(HostBridge.SimTick, clock.Tick);
                }
                Engine_TexLibrary.Pump();
                AttachFrontEnd();
                FollowFrontEnd();
                // The guest steps on its own thread: its pub-game manager is read once a frame.
                Engine_TitleGame.Active.Step();
                if (_world == null && HostBridge.IsInGame)
                {
                    _world = new GameWorld { Name = "GameWorld" };
                    AddChild(_world);
                    _phase = "building the native guest world";
                }
                if (_world?.Entities is { ReadyForRelease: true } && !GameWorld.WorldBuildHeld)
                    _phase = "game world ready";
            }
            // The mods' settings, abilities, errors and godot/*.gd scripts, as GameSession pumps them.
            if (_guest != null)
                Refii.Mods.ModFrame.Pump(this, HostBridge.IsGuestUp, _failure.Length == 0 && _guest.IsInitialized);
        }
        catch (Exception error) { Fail(error); }
        _presentationFrames++;
        _reportAge += delta;
        if (_reportAge < 1) return;
        _reportAge = 0;
        SaveStatus();
        if (_status != null)
        {
            bool gameUiVisible = _ui?.IsRevealed == true || _world?.Entities is { ReadyForRelease: true };
            _status.Visible = !gameUiVisible || _failure.Length != 0;
            _status.Text = _failure.Length == 0 ? _phase : _failure;
        }
    }

    /// <summary>The mod scripts and their singleton go before the guest does.</summary>
    public override void _ExitTree() => Refii.Mods.ModFrame.Stop();

    /// <summary>
    /// Releases held input and holds the guest while the app is away, coming back to the pause
    /// menu; turns the back gesture into the pad's (AndroidSystemKeys). Teardown remains unsupported.
    /// </summary>
    public override void _Notification(int what)
    {
        if (what == NotificationApplicationFocusOut || what == NotificationApplicationPaused)
        {
            _focused = false;
            if (_input != null) _input.DevicesEnabled = false;
            _guest?.ReleaseController();
            if (_guest != null) _guest.Away = true;
            _pauseOnReturn = -1;
        }
        else if (what == NotificationApplicationFocusIn || what == NotificationApplicationResumed)
        {
            _focused = true;
            if (_input != null) _input.DevicesEnabled = true;
            // Focus and resume both arrive on a return: only the first, which finds the guest held, pauses.
            bool wasAway = _guest?.Away == true;
            if (_guest != null) _guest.Away = false;
            if (_ui != null && AndroidSystemKeys.PauseOnReturn(wasAway, HostPanelUp, GameInput.Playing, GameInput.InOverlay,
                    BoardUp, Engine_TitleGame.Active.InteractionDialogUp))
                _pauseOnReturn = PauseOnReturnDelay;
        }
        else if (what == NotificationWMGoBackRequest)
        {
            switch (AndroidSystemKeys.Back(_ui != null, HostPanelUp, GameInput.Playing, GameInput.InOverlay,
                BoardUp, Engine_TitleGame.Active.InteractionDialogUp))
            {
                case AndroidBack.Pause: GameInput.PressOnce(GameInput.PadStart); break;
                case AndroidBack.BackOut: GameInput.PressOnce(GameInput.PadB); break;
                case AndroidBack.ClosePanel:
                    if (GamePhotoMode.Active) GamePhotoMode.RequestBack();
                    else Engine_TitleGame.Active.RequestBack();
                    break;
            }
        }
    }

    /// <summary>Presses Start for a return once the window has its focus back and the world has run a moment.</summary>
    private void PauseOnReturn(double delta)
    {
        if (_pauseOnReturn < 0 || !_focused || !GetWindow().HasFocus()) return;
        _pauseOnReturn -= delta;
        if (_pauseOnReturn >= 0) return;
        if (AndroidSystemKeys.PauseOnReturn(true, HostPanelUp, GameInput.Playing, GameInput.InOverlay,
                BoardUp, Engine_TitleGame.Active.InteractionDialogUp))
            GameInput.PressOnce(GameInput.PadStart);
    }

    /// <summary>Whether photo mode or the mirror owns the screen.</summary>
    private static bool HostPanelUp => GamePhotoMode.Active || Engine_TitleGame.Active.InteractionOwnsDevices;

    /// <summary>Whether a title board (Fable 2's pub-game table) is on screen: a press once reaches it through its own pad.</summary>
    private static bool BoardUp => Engine_TitleGame.Active.InteractionBoardUp;

    /// <summary>Registers the shared mount only after production texture loaders have been installed.</summary>
    private void OnMounted(int count)
    {
        _assetsReady = count > 0;
        if (!_assetsReady) OnMountFailed("No files were mounted from game_root");
    }

    /// <summary>Surfaces mount failures without attempting native initialization.</summary>
    private void OnMountFailed(string reason) => Fail(new InvalidDataException(reason));

    /// <summary>Installs production menus and the input sink after one successful native initialization.</summary>
    private void AttachFrontEnd()
    {
        if (_ui != null || _guest == null) return;
        _input = new GameInput { Name = "GameInput", ControllerStateSink = _guest.PublishController,
            DevicesEnabled = _focused };
        AddChild(_input);
        // Preserve the production order: the menu consumes the same physical sample whose
        // filtered guest packet was published by GameInput earlier in this frame.
        _uiNode = Engine_TitleGame.Active.CreateUi();
        _ui = _uiNode as IGameTitleUi;
        if (_uiNode != null) AddChild(_uiNode);
        // Photo mode, which the desktop's GameSession adds: here it holds this host's own guest thread.
        AndroidNativeSession guest = _guest;
        AddChild(new GamePhotoMode { Name = "PhotoMode", HoldGuest = held => guest.Held = held });
        // The title's own session nodes, which the desktop's GameSession adds: its live UI source,
        // its interactions (a dialog of theirs holds this host's guest too) and, for Fable 2, the
        // pub games — unclaimed, the guest would play its own Scaleform movie unseen behind the
        // loading screen.
        Engine_TitleGame.Active.AddSessionNodes(this, held => guest.Held = held);
    }

    /// <summary>Mirrors real guest UI state; navigation and Load Game come only from the player's input.</summary>
    private void FollowFrontEnd()
    {
        if (_ui == null) return;
        bool found = HostBridge.TryGetFrontEnd(out var screen, out uint state, out bool canPressA,
            out uint cursor, out int slide, out int slideCount);
        if (found) _ui.FollowGuest(screen, slide);
        bool guiUp = HostBridge.FrontEndGuiIsUp;
        bool reveal = InteractiveFrontEndPolicy.ShouldReveal(found, screen, guiUp, canPressA, _ui.HasInGameUi);
        if (reveal) _ui.Reveal();
        else _ui.Conceal("native guest has no visible interactive UI");
        _frontEnd = new { found, screen = screen.ToString(), state, can_press_a = canPressA,
            cursor, scene_slide = slide, slide_count = slideCount, gui_up = guiUp, should_reveal = reveal };
        if (_world == null) _phase = InteractiveFrontEndPolicy.Status(found, screen, guiUp, canPressA);
    }

    /// <summary>Shows boot/configuration errors without introducing game actions.</summary>
    private void BuildStatusOverlay()
    {
        var layer = new CanvasLayer { Name = "AndroidBootStatus", Layer = 100 };
        AddChild(layer);
        _status = new Label { Position = new Vector2(16, 16), Text = _phase,
            MouseFilter = Control.MouseFilterEnum.Ignore };
        _status.AddThemeFontSizeOverride("font_size", 18);
        layer.AddChild(_status);
    }

    /// <summary>Records a bounded error once, preserving ordinary engine diagnostics.</summary>
    private void Fail(Exception error)
    {
        if (_failure.Length != 0) return;
        _failure = error.ToString();
        _phase = "Android startup/presentation failed";
        GD.PrintErr("[android] " + error);
    }

    /// <summary>Atomically writes generic status; counters are evidence of activity, not performance guarantees.</summary>
    private void SaveStatus()
    {
        try
        {
            RefreshRenderScale();
            Viewport viewport = GetViewport();
            Vector2 logicalSize = viewport.GetVisibleRect().Size;
            Vector2 viewportTextureSize = viewport.GetTexture().GetSize();
            float projectScale = ProjectSettings.GetSetting("rendering/scaling_3d/scale", 1.0f).AsSingle();
            Vector2 stretchScale = viewport.GetStretchTransform().Scale;
            Vector2 expectedSize = AndroidRenderScale.ExpectedSize(logicalSize, stretchScale, viewport.Scaling3DScale);
            var sink = _audio?.GetNodeOrNull<Engine_AudioOutput>("AudioOutput");
            var render = Engine_RenderStats.Of(viewport);
            var bankCache = _assets?.NestedBankCache;
            var report = new
            {
                schema = 1, utc = DateTime.UtcNow.ToString("O"), phase = _phase, error = _failure,
                godot = Engine.GetVersionInfo()["string"].AsString(),
                rendering_method = RenderingServer.GetCurrentRenderingMethod(),
                postfx_mode = Refii.Lighting.Engine_PostFxParams.Mode,
                presentation_pacing = new
                {
                    max_fps = Engine.MaxFps,
                    vsync = DisplayServer.WindowGetVsyncMode((int)DisplayServer.MainWindowId).ToString(),
                    target_fps = Engine_PresentationPolicy.TargetFps(true),
                    target_ms = Engine_PresentationPolicy.BudgetMs(true),
                    scope = "App-level presentation settings; Android compositor/display may still pace output; target is diagnostic, not a cap"
                },
                render_resolution = new
                {
                    root_window_width = GetTree().Root.Size.X, root_window_height = GetTree().Root.Size.Y,
                    logical_width = logicalSize.X, logical_height = logicalSize.Y,
                    viewport_texture_width = viewportTextureSize.X, viewport_texture_height = viewportTextureSize.Y,
                    viewport_texture_scope = "Actual GetTexture().GetSize() 2D accessor; Window includes canvas stretch; not RenderingServer or internal 3D buffer size",
                    mesh_lod_threshold = viewport.MeshLodThreshold,
                    configured_mesh_lod_pixels = _meshLod.ConfiguredPixels,
                    configured_mesh_lod_threshold = _meshLod.ParityThreshold,
                    project_3d_scale = projectScale,
                    stretch_x = stretchScale.X, stretch_y = stretchScale.Y,
                    applied_3d_scale = viewport.Scaling3DScale, scaling_mode = viewport.Scaling3DMode.ToString(),
                    expected_3d_width = expectedSize.X, expected_3d_height = expectedSize.Y,
                    estimate_scope = "Expected dimensions before renderer rounding/alignment; not a GPU readback",
                    renderer_buffers = Refii.Lighting.Engine_PostFxEffect.RenderBuffers,
                    renderer_buffers_scope = "Actual active compositor RenderSceneBuffersRD internal/target sizes; detached scalar observation with capture UTC, not a GPU pixel readback",
                    config_path = _runtimeConfig, config_error = _renderScaleError
                },
                focused = _focused, presentation_frames = _presentationFrames,
                assets_ready = _assetsReady, shared_asset_count = _assets?.FileCount ?? 0,
                nested_bank_cache = bankCache,
                nested_bank_cache_scope = "Process-wide BnkFileSystem nested cache; the budget bounds inflated containers only, memory-mapped containers are counted separately",
                guest = _guest?.Describe(), guest_placement = _placement?.Report(), game_ui = _ui?.DescribeInteractiveUi(),
                frontend = _frontEnd, controller = _input?.DescribeControllerInput(),
                world_attached = _world != null, world_held = GameWorld.WorldBuildHeld,
                region_retirement = _world?.RetirementStatus,
                entities_ready = _world?.Entities?.ReadyForRelease ?? false,
                hero = _world?.Entities?.DescribeHero(),
                render = AndroidRenderCounters.Capture(render),
                render_thread = RenderThreadStatus(),
                profiling = new
                {
                    debug_channel = _profiling?.DebugChannel ?? false,
                    handshake_path = _profiling?.HandshakePath ?? string.Empty,
                    gpu_profile_argument_visible = _profiling?.GpuProfileArgumentVisible ?? false,
                    gpu_passes = Engine_GpuPasses.Report(),
                    scope = "gpu_passes.available proves captured timestamps; managed arguments may omit packaged --gpu-profile; debug channel is startup opt-in"
                },
                audio = sink == null ? null : DescribeAudio(sink),
                texture_policy = AndroidTextureUpload.Policy,
                prepared_texture_images = AndroidTextureUpload.PreparedImages,
                texture_upload_payloads = AndroidTextureUpload.Uploads,
                texture_upload_scope = "Cumulative successful upload payload, including replacements; not live VRAM or driver allocation bytes",
                hero_texture_morph = Refii.Hero.Engine_HeroTextureMorph.CacheMetrics,
                hero_texture_morph_scope = "Current decoded dictionary top-level managed RGBA and composite texture references; excludes active callers, temporary Images and textures retained only by live materials; CompositesBuilt is cumulative",
                prepared_gpu_albedos = new { hits = AndroidGpuTextureCache.Hits,
                    misses = AndroidGpuTextureCache.Misses, invalid = AndroidGpuTextureCache.Invalid,
                    payload_bytes = AndroidGpuTextureCache.PayloadBytes },
                automatic_frontend_actions = false,
                touch_controls = new { enabled = _touch?.ControlsEnabled ?? false,
                    context = (_touch?.CurrentContext ?? TouchContext.Hidden).ToString(),
                    touch_mode = Engine_UiInputMode.Touch },
                diagnostic_buttons = new {
                    performance_open = GetNodeOrNull<Refii.Game.GamePerfOverlay>("GamePerfOverlay")?.Visible ?? false,
                    debug_open = GetNodeOrNull<Refii.Game.GameDevMenu>("GameDevMenu")?.IsOpen ?? false,
                    visible = GetNodeOrNull<AndroidDiagnosticButtons>("AndroidDiagnosticButtons")?.Visible ?? false,
                },
                scope = "Foreground controller and touch prototype; explicit movie skipping; guest teardown unsupported"
            };
            string path = Path.Combine(_userDirectory, "android-status.json");
            File.WriteAllText(path + ".tmp", Refii.Core.Engine_Json.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true,
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
            }));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception error) { Fail(error); }
    }

    /// <summary>The render thread this run uses and the one refii.toml asks for (AndroidRenderThread).</summary>
    private object? RenderThreadStatus()
    {
        if (_runtimeConfig.Length == 0 || !File.Exists(_runtimeConfig)) return null;
        return new
        {
            mode = AndroidRenderThread.Describe(Refii.Launcher.Core.RuntimeConfig.ReadValues(_runtimeConfig)),
            write_error = _renderThreadError,
        };
    }

    /// <summary>The guest's output effect: Godot's device layout and rate, and what the effect has pulled.</summary>
    /// <param name="sink">The output node GameAudio created; not ready when the extension failed to load.</param>
    private object DescribeAudio(Engine_AudioOutput sink)
    {
        if (!sink.IsReady)
            return new { ready = false };
        HostBridge.AudioOutputStats(out ulong pulled, out ulong underrunSteps, out uint channels);
        HostBridge.AudioStats(out uint queued, out ulong pushed, out _, out ulong dropped, out _);
        return new
        {
            ready = true, mix_rate = (int)AudioServer.GetMixRate(), speaker_pairs = sink.SpeakerPairs,
            channels, frames_pulled = pulled, frames_pushed = pushed, ring_queued = queued,
            dropped, underrun_steps = underrunSteps,
            guest_frames_per_second = _audio?.GuestFramesPerSecond ?? 0
        };
    }

    /// <summary>Installs the host's single frame-sampler pair and an optional private loopback debug channel.</summary>
    /// <remarks>Android owns sampling even if the listener cannot bind. Its debug server installs
    /// no samplers of its own, so clocks and draw-end callbacks are registered once. Neither path
    /// creates GameSession or initializes, steps or shuts down a native guest. The channel
    /// is read once at startup; an environment variable alone cannot opt Android into it.</remarks>
    private void ConfigureProfiling()
    {
        _profiling = AndroidProfilingOptions.Read(_userDirectory, OS.GetCmdlineArgs());
        _profiling.InstallFrameSampling((handshake, _) =>
        {
            // Do not inherit a desktop/public handshake destination or arbitrary debug port.
            // GameDebugServer binds only 127.0.0.1, scans from 8787 and publishes pid/boot identity.
            SetEnvironment("REFII_DEBUG_HANDSHAKE", handshake);
            SetEnvironment("REFII_DEBUG_PORT", "8787");
            AddChild(new GameDebugServer { Name = "GameDebugServer" });
        }, () =>
        {
            AddChild(new Engine_FrameStartNode { Name = "FrameStart" });
            AddChild(new Engine_FrameEndNode { Name = "FrameEnd" });
        });
    }

    /// <summary>Reloads world resolution and authored mesh LOD quality without changing canvas sizing or visibility ranges.</summary>
    /// <remarks>LOD uses the root Window.Size passed to RenderingServer in CanvasItems mode.
    /// The texture size accessor includes canvas stretch and is reported separately, without a GPU readback.
    /// Unchanged inputs preserve a temporary profiler threshold until it is explicitly restored;
    /// a real quality, extent or global project-scale change reapplies configured parity.</remarks>
    private void RefreshRenderScale()
    {
        if (_runtimeConfig.Length == 0) return;
        try
        {
            // Read the known app-private file directly: changing this diagnostic setting
            // must never select a different config, resize Android's window or start a guest.
            var settings = AndroidRenderScale.ReadSettings(_runtimeConfig);
            // The frame cap follows the same live file, so a changed cap needs no restart.
            if (Engine.MaxFps != settings.MaxFps)
            {
                Engine.MaxFps = settings.MaxFps;
                GD.Print($"[android] frame cap {(settings.MaxFps == 0 ? "off" : settings.MaxFps + " fps")}");
            }
            if (_renderScale.Apply(settings, scale =>
            {
                Viewport viewport = GetViewport();
                if (RenderingServer.GetCurrentRenderingMethod() == "mobile" &&
                    viewport.Scaling3DMode != Viewport.Scaling3DModeEnum.Bilinear)
                    viewport.Scaling3DMode = Viewport.Scaling3DModeEnum.Bilinear;
                viewport.Scaling3DScale = scale;
            })) GD.Print($"[android] root world render scale {_renderScale.Applied:P0}; canvas/UI viewports unchanged");
            ApplyTextureSampling(GetViewport(), settings);
            ApplyColourBuffer(GetViewport(), settings);
            ApplyCanvasLean(settings);
            Viewport lodViewport = GetViewport();
            Vector2I physicalSize = GetTree().Root.Size;
            float projectScale = ProjectSettings.GetSetting("rendering/scaling_3d/scale", 1.0f).AsSingle();
            if (_meshLod.Apply(settings.MeshLodPixels, physicalSize.X, physicalSize.Y, projectScale,
                threshold => lodViewport.MeshLodThreshold = threshold))
                GD.Print($"[android] root mesh LOD threshold {_meshLod.ParityThreshold}; physical root {physicalSize}, project scale {projectScale}; visibility ranges unchanged");
            _renderScaleError = string.Empty;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // An interrupted or invalid external config update keeps the last successful values.
            // Distinct errors are logged once; one file-access problem cannot flood logcat.
            if (_renderScaleError != error.Message) GD.Print("[android] render scale config read failed: " + error.Message);
            _renderScaleError = error.Message;
        }
    }

    /// <summary>
    /// `canvas_lean`, applied when the setting changes, so the debug channel's live lever
    /// (`canvasLean`) holds until the file says otherwise. See Engine_CanvasLean.
    /// </summary>
    private void ApplyCanvasLean(GameWindowSettings settings)
    {
        if (_canvasLeanSetting == settings.CanvasLean) return;
        _canvasLeanSetting = settings.CanvasLean;
        Engine_CanvasLean.Enabled = settings.CanvasLean;
        GD.Print($"[android] canvas_lean {(settings.CanvasLean ? "on" : "off")}");
    }

    /// <summary>GameUi's UI layer, the canvas the touch controls may share; null until the UI stands.</summary>
    private CanvasLayer? UiLayer()
    {
        if (_uiLayer is not null && GodotObject.IsInstanceValid(_uiLayer)) return _uiLayer;
        return _uiLayer = _uiNode is not null && GodotObject.IsInstanceValid(_uiNode) ? _uiNode.GetNodeOrNull<CanvasLayer>("UiLayer") : null;
    }

    /// <summary>
    /// The world's texture sampling at the applied render scale: the mip bias Godot adds for
    /// it cancelled, and the anisotropic filtering setting applied.
    /// </summary>
    /// <remarks>
    /// GODOT SAMPLES A SCALED WORLD AS IF IT WERE FULL SIZE. It adds log2(scale) to every
    /// mipmapped sampler's bias (renderer_viewport.cpp), which pays off only when TAA or FSR 2
    /// rebuilds the detail. Android upscales bilinearly with neither, so at 35% the bias of
    /// -1.5 fetched about eight times the texels per pixel, for shimmer and memory traffic.
    /// The player's `texture_mipmap_bias` is added on top. `anisotropic_filtering` was never
    /// applied on Android (GameWindow is desktop-only), leaving Godot's 4x on every surface.
    /// </remarks>
    /// <summary>
    /// `hdr_2d`: whether the root viewport keeps Godot's RGBA16F targets or, off, the Mobile
    /// renderer's 10-bit 3D buffer and an RGBA8 2D target: half the colour bandwidth and tile
    /// memory, for highlights that clip a little sooner. On a Galaxy S10e (Adreno 640) off took 6% off the GPU's work in Bowerstone
    /// Market, the native-resolution canvas pass halving. A change rebuilds every pipeline once,
    /// a stall of some seconds, so it is a launch or preset change rather than a live lever.
    /// </summary>
    private static void ApplyColourBuffer(Viewport viewport, GameWindowSettings settings)
    {
        if (viewport.UseHdr2D != settings.Hdr2D)
        {
            viewport.UseHdr2D = settings.Hdr2D;
            GD.Print($"[android] world colour buffer {(settings.Hdr2D ? "RGBA16F" : "RGB10A2, canvas RGBA8")}");
        }
    }

    private static void ApplyTextureSampling(Viewport viewport, GameWindowSettings settings)
    {
        float bias = -MathF.Log2(MathF.Min(viewport.Scaling3DScale, 1f)) + settings.MipmapBias;
        if (MathF.Abs(viewport.TextureMipmapBias - bias) > 0.001f)
        {
            viewport.TextureMipmapBias = bias;
            GD.Print($"[android] texture mip bias {bias:0.00} at render scale {viewport.Scaling3DScale:P0}");
        }

        Viewport.AnisotropicFiltering aniso = settings.Anisotropic switch
        {
            >= 16 => Viewport.AnisotropicFiltering.Anisotropy16X,
            >= 8 => Viewport.AnisotropicFiltering.Anisotropy8X,
            >= 4 => Viewport.AnisotropicFiltering.Anisotropy4X,
            >= 2 => Viewport.AnisotropicFiltering.Anisotropy2X,
            _ => Viewport.AnisotropicFiltering.Disabled,
        };
        if (viewport.AnisotropicFilteringLevel != aniso)
        {
            viewport.AnisotropicFilteringLevel = aniso;
            GD.Print($"[android] anisotropic filtering {aniso}");
        }
    }

    /// <summary>Updates managed and libc environment views before either side caches its configuration.</summary>
    private static void SetEnvironment(string name, string value)
    {
        System.Environment.SetEnvironmentVariable(name, value);
        if (Refii.Core.Engine_Platform.IsAndroid && setenv(name, value, 1) != 0)
            throw new InvalidOperationException("Android setenv failed for " + name);
    }
}
