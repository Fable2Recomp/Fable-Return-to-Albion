using Refii.Assets;
using Refii.Bnk;
using Refii.Game;
using Refii.Host;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Refii.AndroidRuntime;

/// <summary>Owns native initialization, all service steps and pad-zero publication on one OS thread.</summary>
/// <remarks>Do not add GameSession alongside this owner. Native remote-thread termination is
/// not implemented on Android; this prototype supports one foreground boot per process.</remarks>
internal sealed class AndroidNativeSession
{
    private readonly AndroidRuntimeOptions _options;
    private readonly string _appRoot;
    private readonly Engine_GameAssets _assets;
    private readonly SinglePlayerControllerMailbox _controller = new();
    private readonly AndroidMovieSkipPolicy _movieSkips, _unshownMovieSkips;
    private readonly AndroidMovieSkipPolicy _aheadMovieSkips = new(true, AheadMovieDwellSeconds);
    private readonly bool _realClock;
    private Thread? _thread;
    private volatile bool _initialized;
    private volatile string _phase = "created";
    private volatile string _error = string.Empty;
    private volatile string _import = "not attempted";
    private long _steps, _moviesSkipped;

    /// <summary>
    /// Holds the guest still while true (photo mode): no step runs, as under GameSession.Paused on
    /// the desktop, and the 30 Hz clock starts afresh on release so no steps are replayed.
    /// </summary>
    internal volatile bool Held;

    /// <summary>Holds the guest still while the app is away (a call, the shade, another app), apart from photo mode's hold.</summary>
    internal volatile bool Away;

    /// <summary>The host plays the guest's movies (AndroidStartup's GameVideo): one it has not
    /// picked up is retired only after <see cref="UnshownMovieDwellSeconds"/>, so a main-thread
    /// hitch while the guest boots does not cost a startup video.</summary>
    internal volatile bool HostPlaysMovies;

    /// <summary>The host is showing the requested movie: it ends it (its end, or a tap), never the dwell.</summary>
    internal volatile bool HostShowsMovie;

    /// <summary>The dwell for a movie the host plays but has not shown: one it could not decode.</summary>
    public const double UnshownMovieDwellSeconds = 10;

    /// <summary>The sizes of the startup videos the host plays ahead of the guest (GameVideo.PlayAhead).
    /// The guest's own request for one is retired after <see cref="AheadMovieDwellSeconds"/>, as a tap
    /// would, whatever <c>skip_movies</c> says: the host shows it, before or after.</summary>
    internal volatile System.Collections.Generic.IReadOnlySet<uint>? AheadSizes;

    /// <summary>The shortest dwell the skip policy takes, about when a tap may first skip a movie.</summary>
    public const double AheadMovieDwellSeconds = 0.5;

    [DllImport("refii_hostbridge", CallingConvention = CallingConvention.Cdecl)]
    private static extern int RefiiHostSetGameModulePath(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string absolutePath, byte[] message, nuint capacity);

    /// <summary>
    /// <c>android_guest_real_clock</c>: the guest reads the real clock instead of the driven time
    /// base's watchdog.
    /// </summary>
    /// <remarks>
    /// WHY. The native time base starts in Driven mode, where time moves only when the host grants
    /// steps; the desktop's GameSession grants them, and nothing here does. So 267 ms after boot
    /// (eight steps without a grant) the watchdog takes over for good and runs the clock at the
    /// real rate, and from then on every guest <c>mftb</c> on every guest thread takes the time
    /// base's mutex and writes two shared counters (rex_bridge_host.cpp, the locked read): the
    /// game thread's own clock reads contend with the 3D Engine thread's and the audio threads'.
    /// On answers the same real-rate clock with one relaxed load. Set before the guest boots, so
    /// no guest thread ever sees the frozen first 267 ms or a change of origin.
    /// </remarks>
    public const string RealClockKey = "android_guest_real_clock";

    /// <summary>Creates one owner for an already registered production asset mount.</summary>
    internal AndroidNativeSession(AndroidRuntimeOptions options, string appRoot, Engine_GameAssets assets,
        bool realClock = false)
    {
        _options = options;
        _appRoot = appRoot;
        _assets = assets;
        _realClock = realClock;
        _movieSkips = new(options.SkipMovies, options.MovieDwellSeconds);
        _unshownMovieSkips = new(options.SkipMovies, Math.Max(options.MovieDwellSeconds, UnshownMovieDwellSeconds));
    }

    /// <summary>Whether refii.toml asks for the real clock; off when unset.</summary>
    public static bool RealClockConfigured(System.Collections.Generic.IReadOnlyDictionary<string, string> values) =>
        Refii.Core.Engine_RefiiConfig.Bool(values, RealClockKey, false);

    /// <summary>True only after successful native initialization.</summary>
    internal bool IsInitialized => _initialized;

    /// <summary>The last boot/step phase, published independently of Godot's main thread.</summary>
    internal string Phase => _phase;

    /// <summary>A managed or native boot failure, empty during ordinary execution.</summary>
    internal string Error => _error;

    /// <summary>The uncapped number of native service steps completed.</summary>
    internal long StepsCompleted => Interlocked.Read(ref _steps);

    /// <summary>Starts at most one native execution owner.</summary>
    internal void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "RefiiAndroidGuest" };
        _thread.Start();
    }

    /// <summary>Accepts a coherent production input sample without becoming a second native writer.</summary>
    internal void PublishController(GameControllerPacket packet) => _controller.Publish(packet);

    /// <summary>Releases held input on Android focus loss through the same mailbox.</summary>
    internal void ReleaseController() => PublishController(new(0, false, 0, 0, 0, 0, 0, 0, 0));

    /// <summary>Returns bounded boot and input diagnostics without inspecting private save contents.</summary>
    internal object Describe() => new
    {
        phase = _phase, initialized = _initialized, error = _error, steps = StepsCompleted,
        save_import = _import, movies_skipped = Interlocked.Read(ref _moviesSkipped),
        skip_movies = _options.SkipMovies, latest_controller = _controller.Latest,
        owner_controller = _controller.Owner,
        controller_read_receipts = _controller.DescribeReceipts(),
        latest_controller_age_ms = SinglePlayerControllerMailbox.AgeMilliseconds(_controller.Latest)
    };

    /// <summary>Binds the shared mount, initializes native state and paces the guest without catch-up bursts.</summary>
    private void Run()
    {
        try
        {
            _phase = "checking private inputs";
            if (!Directory.Exists(_options.GameRoot) || !File.Exists(Path.Combine(_options.GameRoot, "default.xex")))
                throw new InvalidDataException("game_root must contain the user's prepared default.xex and data directory");
            if (!File.Exists(_options.ModulePath))
                throw new FileNotFoundException("Android ARM64 game module is missing", _options.ModulePath);
            // The app's save folder; the active profile in it is what HostBridge.Initialize states as --user_data_root.
            string saves = Refii.Core.Engine_AppRoot.SavesDirectory;
            AndroidSaveTransfer.GameStarted = true;
            AndroidSaveImporter.Result imported = AndroidSaveImporter.ImportIfEmpty(_options.SaveImportRoot, saves);
            _import = imported.Status;
            int? abi = HostBridge.TryGetRawAbiVersion();
            if (abi != HostBridge.ExpectedAbiVersion)
                throw new InvalidDataException($"Host bridge ABI {abi}, expected {HostBridge.ExpectedAbiVersion}");
            byte[] message = new byte[1024];
            if (RefiiHostSetGameModulePath(_options.ModulePath, message, (nuint)message.Length) != 1)
                throw new InvalidDataException("Game module path refused: " + Decode(message));
            if (!_assets.IsMounted)
                throw new InvalidOperationException("Production texture and asset readers must be registered before native boot");
            HostBridge.SetAssetReader(ReadAsset, AssetSize);
            HostBridge.SetAssetOverrideQuery(_assets.FileIsOverride);
            HostBridge.SetModTextLookup(text => Refii.Mods.ModText.Text(text));
            HostBridge.SetAssetNameResolver(AssetHashIndex.PathForHash);
            if (_realClock)
            {
                var timeBase = HostBridge.TimeBase(HostBridge.TimeBaseMode.Off);
                Godot.GD.Print($"[android] guest time base: {timeBase?.Mode.ToString() ?? "unavailable"} ({RealClockKey})");
            }

            _phase = "initializing native guest";
            // HostBridge states user_data_root, content_title_folders and cache_root itself; stating one
            // again here fails the whole command line's parse, and the saves' folders with it.
            var result = HostBridge.Initialize(_options.GameRoot,
                "--log_file=" + Path.Combine(_appRoot, "logs", "refii.log"));
            if (!result.Success) throw new InvalidOperationException(result.Message);
            _initialized = true;
            _phase = "running native guest";
            var clock = Stopwatch.StartNew();
            double deadline = 0;
            while (true)
            {
                if (Held || Away)
                {
                    Thread.Sleep(10);
                    deadline = clock.Elapsed.TotalMilliseconds;
                    continue;
                }

                ulong receipt = HostBridge.GetControllerReadReceipt(0);
                GameControllerPacket packet = _controller.SelectAcknowledged(StepsCompleted + 1, receipt, out ulong token);
                HostBridge.SetControllerStateTracked(0, packet.Connected, packet.Buttons, packet.LeftTrigger,
                    packet.RightTrigger, packet.LeftX, packet.LeftY, packet.RightX, packet.RightY, token);
                bool running = HostBridge.Step();
                Interlocked.Increment(ref _steps);
                if (!running) { _phase = "native guest stopped"; break; }
                SkipRequestedMovie();
                deadline += 1000.0 / 30;
                double remaining = deadline - clock.Elapsed.TotalMilliseconds;
                if (remaining > 1) Thread.Sleep((int)Math.Min(remaining, 34));
                else if (remaining < -100) deadline = clock.Elapsed.TotalMilliseconds;
            }
        }
        catch (Exception error)
        {
            _error = error.ToString();
            _phase = "native session failed";
        }
        finally
        {
            ReleaseController();
            if (_initialized) HostBridge.SetControllerState(0, false, 0, 0, 0, 0, 0, 0, 0);
            // Shutdown must not terminate Mono threads from this guest thread. Android's
            // remote-thread termination/lifecycle contract requires a separate native fix.
        }
    }

    /// <summary>Reads real game bytes from the single production mount used by GameUi and GameWorld.</summary>
    /// <remarks>The mount owns the complete read so a replacement cannot dispose a nested archive
    /// after lookup but before its payload has been copied for the native guest.</remarks>
    /// <param name="path">The guest's logical asset path.</param>
    /// <returns>The current mounted payload, or null when the path is absent.</returns>
    private byte[]? ReadAsset(string path) => _assets.ReadAllBytes(path);

    /// <summary>Answers native size probes without reading whole archive entries.</summary>
    private long AssetSize(string path) => _assets.TryGetFile(path, out _) ? _assets.FileSize(path) : -1;

    /// <summary>Retires explicit movie requests after a configured dwell; it sends no frontend/menu action.</summary>
    /// <remarks>Video playback is not provided by this entry point, apart from the startup videos
    /// AndroidStartup plays (<see cref="HostPlaysMovies"/>). Keeping this configured policy in
    /// configuration distinguishes skipped introductory video from decoded video or title automation.</remarks>
    private void SkipRequestedMovie()
    {
        if (HostShowsMovie || HostBridge.MovieState() != HostBridge.MoviePlayback.Wanted) return;
        uint embeddedSize = HostBridge.GetRequestedVideoSize(out uint bufferGeneration);
        bool ahead = embeddedSize != 0 && AheadSizes?.Contains(embeddedSize) == true;
        if (!ahead && !_options.SkipMovies) return;
        string path = HostBridge.GetRequestedVideoName(out uint nameGeneration);
        uint generation = path.Length != 0 ? nameGeneration : bufferGeneration;
        double seconds = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        if ((ahead ? _aheadMovieSkips : HostPlaysMovies ? _unshownMovieSkips : _movieSkips).ShouldFinish(true, generation, embeddedSize != 0 || path.Length != 0, seconds) &&
            HostBridge.FinishMovie() != 0) Interlocked.Increment(ref _moviesSkipped);
    }

    /// <summary>Decodes one bounded native UTF-8 diagnostic buffer.</summary>
    private static string Decode(byte[] bytes)
    {
        int length = Array.IndexOf(bytes, (byte)0);
        return Encoding.UTF8.GetString(bytes, 0, length < 0 ? bytes.Length : length);
    }
}
