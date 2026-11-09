using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Refii.Game;
using Refii.Models;

namespace Refii.AndroidRuntime;

/// <summary>
/// From the game scene's start to the front end: the desktop's startup screen (the seal over moving
/// smoke, with its music) and the guest's startup videos, which a tap skips.
/// </summary>
/// <remarks>
/// <para>The screen stays up through the mount, the guest's boot and its startup sequence, and goes
/// for good once <see cref="Finished"/> holds, the condition that hides the host's status line. While
/// a video plays it stays drawn under it and its music pauses.</para>
/// <para>The videos are the desktop's <see cref="GameVideo"/>, which ends a movie on a click, so on a
/// touch screen on a tap (Godot's touch-to-mouse emulation). It is only here until the front end:
/// later movies are still retired after the configured dwell, as before
/// (<see cref="AndroidNativeSession.HostPlaysMovies"/>).</para>
/// <para>PLAYED AHEAD. The guest only asks for its startup videos once the mount and its own boot are
/// done (about 10 s on a Galaxy S10e), and does little behind them: the front end showed 3 s after
/// the last one. So the videos it asked for last time (<see cref="LearnedFile"/>, loose files under
/// art/videos) start as soon as this scene does, and the guest's own requests for them are retired at
/// once (AndroidNativeSession.AheadSizes): the loading happens behind the videos. A first start, or a
/// new install, plays them when the guest asks, and learns them.</para>
/// <para>The clear colour is black until the world is up, as the screen's ground: the front end's first
/// page (the legal text) has none of its own, and showed it grey for a moment.</para>
/// </remarks>
public partial class AndroidStartup : Node
{
    /// <summary>The video layer, just over the startup screen (145), which stays drawn under it.</summary>
    private const int VideoLayer = 146;

    /// <summary>The launcher's Skip intro videos. Off unless the player turns it on.</summary>
    public const string SkipVideosKey = "android_skip_intro_videos";

    private bool _skip;

    /// <summary>Whether the player asked to skip the startup videos.</summary>
    /// <param name="values">The runtime configuration's values.</param>
    public static bool SkipsVideos(System.Collections.Generic.IReadOnlyDictionary<string, string> values) =>
        Refii.Core.Engine_RefiiConfig.Bool(values, SkipVideosKey, false);

    /// <summary>The startup videos the guest asked for last time, one file name a line, in the title's directory.</summary>
    public const string LearnedFile = "startup-videos.txt";

    /// <summary>Whether the front end, the world or an error has the screen now.</summary>
    public Func<bool> Finished { get; set; } = static () => false;

    /// <summary>Whether the game world is up, after which the clear colour is the project's again.</summary>
    public Func<bool> InWorld { get; set; } = static () => false;

    /// <summary>Whether the guest is up and its front end not attached yet (AndroidGameHost.AttachFrontEnd):
    /// the next video played ahead waits a frame for it.</summary>
    public Func<bool> FrontEndPending { get; set; } = static () => false;

    /// <summary>
    /// Whether a startup video is on screen, so the front end is not attached yet.
    /// </summary>
    /// <remarks>ATTACHING THE FRONT END IS ONE LONG FRAME (the game UI, its HUD and the title's
    /// template trees: 1.5-1.7 s on a OnePlus 7 Pro and a Galaxy S10e). Done when the guest came up,
    /// it froze the first video in the middle; held to the gap between two videos, the same frame
    /// is a pause between the logos, and the videos end no later than before.
    /// <para>And until the startup screen has been drawn once since the video ended. The video goes
    /// in the frame after this host's turn, so that frame is drawn with neither the video nor the
    /// screen: attached at once, the long frame kept it on screen, black, for the whole pause.</para></remarks>
    public bool HoldsFrontEnd => !_done && (_video.IsPlaying || _screenUpAt < 0 || Engine.GetFramesDrawn() <= _screenUpAt + 1);

    /// <summary>Engine.GetFramesDrawn() when the startup screen last came back, or -1 while a video has the screen.</summary>
    private long _screenUpAt = -1;

    private AndroidNativeSession? _guest;
    private GameVideo _video = null!;
    private bool _done;
    private int _guestBus = -1;
    private bool _guestMuted;

    private readonly Queue<byte[]> _queue = new();
    private readonly HashSet<uint> _aheadSizes = new();
    private Task<Videos>? _loading;
    private Dictionary<long, string> _names = new();
    private readonly List<string> _asked = new();
    private List<string> _previous = new();
    private string _learned = string.Empty;

    /// <summary>Last time's startup videos, read, and every art/videos file's name by size.</summary>
    private sealed record Videos(List<string> Learned, List<byte[]> Ahead, Dictionary<long, string> Names);

    /// <summary>Black until the world is up; the video player, and last time's videos read from the game data.</summary>
    public override void _Ready()
    {
        // After the video's own turn, so the screen is back in the frame a video ends or is skipped:
        // before it, that frame showed the touch controls and the status line on black.
        ProcessPriority = 1;
        RenderingServer.SetDefaultClearColor(Colors.Black);
        _video = new GameVideo { Name = "GameVideo" };
        AddChild(_video);
        _video.GetNode<CanvasLayer>("VideoLayer").Layer = VideoLayer;
        // Its black backdrop follows the picture from its first update on; before that it covered the screen for a frame.
        _video.GetNode<CanvasItem>("VideoLayer/VideoBackdrop").Visible = false;
        _video.GuestRequested = Asked;
        _skip = SkipsVideos(Refii.Core.Engine_RefiiConfig.Read(out _));
        if (_skip)
        {
            // Nothing is played or learned: the guest's requests are retired in its session.
            _video.SetProcess(false);
            return;
        }

        string learned = _learned = Path.Combine(AndroidRuntimeOptions.TitleDirectory(OS.GetUserDataDir()), LearnedFile);
        string root = Refii.Core.Engine_GameData.Resolve();
        _loading = Task.Run(() => Load(root, learned));
    }

    /// <summary>Off the main thread: the learned videos' bytes, in their order, and the names of art/videos by size.</summary>
    private static Videos Load(string root, string learned)
    {
        var videos = new Videos(new(), new(), new());
        var library = Refii.Video.Engine_VideoLibrary.Load(root);
        string folder = Path.Combine(library.Directory, "art", "videos");
        if (library.Directory.Length == 0 || !Directory.Exists(folder)) return videos;
        foreach (string path in Directory.EnumerateFiles(folder, "*.bik"))
            videos.Names.TryAdd(new FileInfo(path).Length, Path.GetFileName(path));
        if (!File.Exists(learned)) return videos;
        foreach (string name in File.ReadAllLines(learned).Select(line => line.Trim()).Where(line => line.Length > 0))
        {
            videos.Learned.Add(name);
            string path = Path.Combine(folder, Path.GetFileName(name));
            if (File.Exists(path)) videos.Ahead.Add(File.ReadAllBytes(path));
        }
        return videos;
    }

    /// <summary>Records a startup video the guest asked for, by its size among art/videos.</summary>
    private void Asked(byte[] bytes)
    {
        if (_names.TryGetValue(bytes.Length, out string? name) && !_asked.Contains(name)) _asked.Add(name);
    }

    /// <summary>The guest is booting: its session shows the startup videos through this player and retires its requests for those played ahead.</summary>
    internal void PlayVideos(AndroidNativeSession guest)
    {
        if (_done) return;
        _guest = guest;
        guest.HostPlaysMovies = !_skip;
        guest.AheadSizes = _aheadSizes;
        guest.RetireEveryMovie = _skip;
    }

    /// <summary>Plays the videos ahead, hides the screen while one plays, and ends the startup once the game has the screen and no video plays.</summary>
    public override void _Process(double delta)
    {
        if (_done)
        {
            if (!InWorld()) return;
            RenderingServer.SetDefaultClearColor(
                ProjectSettings.GetSetting("rendering/environment/defaults/default_clear_color").AsColor());
            QueueFree();
            return;
        }

        TakeLoaded();
        if (!_video.IsPlaying && _queue.Count > 0 && !FrontEndPending()) _video.PlayAhead(_queue.Dequeue());
        bool playing = _video.IsPlaying;
        bool ahead = _video.PlayingAhead;
        if (_guest != null) _guest.HostShowsMovie = playing && !ahead;
        // The guest's own sound (the front end's music once it is up) waits for a video played ahead.
        MuteGuest(ahead);
        if (playing || !Finished())
        {
            // UNDER THE VIDEOS, NOT HIDDEN: shown again only once a video was gone, its first frame back
            // was black, and the frame before a video's first picture showed the touch controls and the
            // status line. Silent while a video plays and between two: the music would play for the pause and stop.
            Engine_ShaderWarmup.ShowStartupScreen(true, music: !playing && _queue.Count == 0);
            if (playing) _screenUpAt = -1;
            else if (_screenUpAt < 0) _screenUpAt = Engine.GetFramesDrawn();
            return;
        }

        _done = true;
        Engine_ShaderWarmup.ReleaseScreen();
        MuteGuest(false);
        if (_guest != null)
        {
            _guest.HostShowsMovie = _guest.HostPlaysMovies = _guest.RetireEveryMovie = false;
            _guest.AheadSizes = null;
        }
        GD.Print($"[android] startup over at {Time.GetTicksMsec()} ms: {_video.MoviesRequested} video(s), " +
            $"{_video.MoviesSkipped} skipped; the guest asked for [{string.Join(", ", _asked)}]");
        if (!_skip) Learn();
        _video.QueueFree();
    }

    /// <summary>Queues last time's videos once they are read; their sizes go to the guest's session.</summary>
    private void TakeLoaded()
    {
        if (_loading is not { IsCompleted: true } loading) return;
        _loading = null;
        if (loading.IsFaulted)
        {
            GD.PushWarning("[android] startup videos not read: " + loading.Exception?.GetBaseException().Message);
            return;
        }

        Videos videos = loading.Result;
        _names = videos.Names;
        _previous = videos.Learned;
        foreach (byte[] bik in videos.Ahead)
        {
            _video.ExpectAhead(bik);
            _queue.Enqueue(bik);
            _aheadSizes.Add((uint)bik.Length);
        }
        if (videos.Ahead.Count > 0) GD.Print($"[android] playing {videos.Ahead.Count} startup video(s) ahead of the guest");
    }

    /// <summary>Adds the videos the guest asked for that the file does not name yet, in the guest's order, for the next start.</summary>
    /// <remarks>Only adds: the guest's request for a video played ahead is retired at once, and one
    /// made in the 2 s frame its boot takes can be gone before the player sees it. A learned video
    /// the guest was not seen asking for keeps its place before the next one it was.</remarks>
    private void Learn()
    {
        List<string> learned = Merge(_previous, _asked);
        if (learned.SequenceEqual(_previous)) return;
        try { File.WriteAllLines(_learned, learned); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            GD.PushWarning("[android] startup videos not recorded: " + error.Message);
        }
    }

    /// <summary>The learned videos with those the guest asked for added, in the guest's order.</summary>
    public static List<string> Merge(IReadOnlyList<string> previous, IReadOnlyList<string> asked)
    {
        var learned = new List<string>();
        int kept = 0;
        foreach (string name in asked)
        {
            int at = previous.ToList().FindIndex(known => known.Equals(name, StringComparison.OrdinalIgnoreCase));
            for (; kept < at; kept++) if (!learned.Contains(previous[kept])) learned.Add(previous[kept]);
            kept = Math.Max(kept, at + 1);
            if (!learned.Contains(name, StringComparer.OrdinalIgnoreCase)) learned.Add(name);
        }
        for (; kept < previous.Count; kept++) if (!learned.Contains(previous[kept])) learned.Add(previous[kept]);
        return learned;
    }

    /// <summary>Mutes or unmutes the guest's own bus, once it exists.</summary>
    private void MuteGuest(bool mute)
    {
        if (mute == _guestMuted) return;
        if (_guestBus < 0) _guestBus = AudioServer.GetBusIndex("GuestAudio");
        if (_guestBus < 0) return;
        AudioServer.SetBusMute(_guestBus, mute);
        _guestMuted = mute;
    }
}
