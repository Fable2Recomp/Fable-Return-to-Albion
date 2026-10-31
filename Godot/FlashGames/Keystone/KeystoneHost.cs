using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Refii.Core;
using Refii.Ui.Flash;
using Refii.FlashGames.Framework;
using Refii.Textures;

namespace Refii.FlashGames.Keystone;

public partial class KeystoneHost : Node, IPubGameSession
{
    [Export]
    public string GameRoot { get; set; } = "assets/game";

    [Export]
    public string Language { get; set; } = "en";

    [Export]
    public int RenderWidth { get; set; } = 640;

    [Export]
    public int RenderHeight { get; set; } = 462;

    [Export]
    public int AiPlayers { get; set; } = Unset;

    [Export]
    public ulong Seed { get; set; }

    [Export]
    public int Skin { get; set; } = Unset;

    [Export]
    public string Variant { get; set; } = "Standard1";

    [Export]
    public int BuyIn { get; set; } = Unset;

    [Export]
    public int StartingGold { get; set; } = 2000;

    /// <summary>The guest's player, as <c>sub_82EF18A0</c> hands it over; null in a harness.</summary>
    public PubGameSeed? Profile { get; set; }

    /// <summary>Where the game's fscommands go besides the trace: the guest, in a session.</summary>
    public IPubGameHost? Guest { get; set; }

    public const int Unset = -1;

    [Export]
    public bool ShowDiagnostics { get; set; } = true;

    public const string GameName = "Keystone";

    public const int UnresolvedLightingDepth = 3;

    private Keystone _keystone = null!;
    private GameBoard _board = null!;
    private KeystonePlayer _human = null!;
    private Node2D _view = null!;
    private KeystoneOverlayManager _overlays = null!;
    private Label _status = null!;
    private PubStopwatchTimer _timer = null!;
    private PubGameCommandSink _commands = null!;

    private SwfSceneUi? _sceneUi;
    private SwfStage? _stage;
    private SwfScene? _scene;
    private IPubClip _root = null!;
    private Vector2 _stageSize = new(640f, 462f);

    private bool _autoOpenBank =
        System.Environment.GetEnvironmentVariable("REFII_KEYSTONE_OPEN_BANK") is "1";

    private double _fpsAccumulator;
    private int _fpsFrames;
    private double _fps;
    private bool _ready;
    private bool _failed;

    public SwfStage? Stage => _stage;

    public SwfScene? Scene => _scene;

    public SwfSceneUi? SceneUi => _sceneUi;

    public KeystoneOverlayManager Overlays => _overlays;

    /// <summary>The board's timelines freed with it, when <c>pubgame_release_on_close</c> is on.</summary>
    public override void _Notification(int what)
    {
        if (what == NotificationPredelete && Engine_TexSettings.PubGameReleaseOnClose)
        {
            _sceneUi?.Release();
        }
    }

    public override void _Ready()
    {
        Engine_TexEnhance.EnsureRegistered();

        _view = new Node2D();
        AddChild(_view);

        _status = new Label { Position = new Vector2(8, 8), Visible = ShowDiagnostics };
        AddChild(_status);

        if (!Load())
        {
            string message = "pub games not found under " + GameRoot;
            _status.Text = message;

            _status.Visible = true;
            GD.PushWarning($"[pubgame] {message}");
            _failed = true;
            return;
        }

        _ready = true;
    }

    private bool Load()
    {
        string root = ResolveRoot();
        PubGamesSet? set = PubGamesCatalog.Discover(root);
        PubGameEntry? game = set?.Games.FirstOrDefault(g => g.Name.Contains("keystone"));
        if (set is null || game is null)
        {
            return false;
        }

        _timer = new PubStopwatchTimer();

        var random = new PubRandom(ResolveSeed());
        _commands = new PubGameCommandSink { Forward = Guest };
        _keystone = new Keystone(_commands, _timer, random);

        LoadLocalization(set, game);
        List<string> applied = ApplySettings(set, game);
        GD.Print($"[pubgame] settings: {string.Join(", ", applied)}");
        WarnIfTableMissing(applied);

        if (SwfUiForced() || !LoadSceneUi(root))
        {
            if (!LoadSwfStage(set, game))
            {
                return false;
            }
        }

        foreach (IPubClip child in RootChildren())
        {
            if (child.Name is "jackpot")
            {
                child.Visible = false;
            }
        }

        IPubClip? betStats = _root.GetChild("betStats");
        if (betStats is not null)
        {
            betStats.Alpha = 100;
        }

        // The guest's order: `pubGame.Initialize` from sub_82EF0528 (0x82ef0b68), then
        // sub_82EF18A0's AddHumanPlayer and settings, then `pubGame.Run`. Run lends the buy-in,
        // seats the 83 NPCs, allows points and calls PostInitialize, which builds the board.
        _overlays = new KeystoneOverlayManager(_keystone, _root);
        _keystone.OverlayManager = _overlays;
        _keystone.Root = _root;
        _keystone.Initialize();

        PubGameSeed seed = Profile ?? PubGameSeed.Default(StartingGold);
        _human = (KeystonePlayer)seed.CreateHuman(_keystone);
        seed.ApplyAfterSettings(_keystone);
        _keystone.Run();
        _board = _keystone.Board!;

        foreach (IPubClip child in RootChildren())
        {
            if (child.GetDepth() == UnresolvedLightingDepth)
            {
                child.Visible = false;
            }
        }

        GD.Print(
            $"[pubgame] {game.Name} from {set.Root}: " +
            (_sceneUi is not null
                ? $"scene {_sceneUi.ScenePath}, {_stageSize.X:0}x{_stageSize.Y:0} stage"
                : $"swf stage, {(_scene!.Vector ? "vector geometry" : "rasterized artwork")}, " +
                  $"upscaler {Engine_TexEnhance.BackendId}"));

        return true;
    }

    private bool LoadSceneUi(string root)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        if (Refii.Assets.Engine_GameAssets.Instance is not { IsMounted: true })
        {
            string pubGames = PubGamesCatalog.ResolveRoot(root);
            Refii.Bnk.BnkFileSystem.Mount(pubGames.Length != 0 ? pubGames : root);
            Engine_SwfAssets.EnsureLoaders();
        }

        double mounted = clock.Elapsed.TotalMilliseconds;
        _sceneUi = SwfSceneUi.Load(GameName);
        if (_sceneUi is null)
        {
            return false;
        }

        double loaded = clock.Elapsed.TotalMilliseconds;
        _view.AddChild(_sceneUi.Node);
        _root = _sceneUi.Root;
        _stageSize = _sceneUi.StageSize;
        GD.Print($"[pubgame] scene ui: mount {mounted:0} ms, load+instantiate {loaded - mounted:0} ms, " +
                 $"in tree {clock.Elapsed.TotalMilliseconds - loaded:0} ms");
        return true;
    }

    private bool LoadSwfStage(PubGamesSet set, PubGameEntry game)
    {
        SwfFile? file = SwfFile.TryLoad(File.ReadAllBytes(game.SwfPath), out string error);
        if (file is null)
        {
            _status.Text = "swf load failed: " + error;
            return false;
        }

        SwfDocument document = SwfDocument.Build(file);
        PubGameTextures textures = PubGameTextures.Build(set, game, document, _keystone.SkinIndex);
        set.SharedFonts.TryGetValue(Language, out string? pack);
        PubGameFonts fonts = PubGameFonts.Build(document, pack ?? string.Empty, Language);

        _stage = new SwfStage(document, file.FrameSize, textures.Resolve) { Fonts = fonts };
        _root = _stage.Root;
        _stageSize = new Vector2(
            Mathf.Max(file.FrameSize.Width, 1) / SwfShapeGeometry.TwipsPerPixel,
            Mathf.Max(file.FrameSize.Height, 1) / SwfShapeGeometry.TwipsPerPixel);

        _scene = new SwfScene(_stage, _view)
        {
            Vector = Engine_TexSettings.PubGameVectorShapes,
        };

        GD.Print($"[pubgame] textures: {textures.Bindings.Count} bound, {textures.Unbound.Count} unbound");
        return true;
    }

    private void AdvanceUi(double delta)
    {
        _stage?.Advance();
        _sceneUi?.Advance(delta);
    }

    private IEnumerable<IPubClip> RootChildren()
    {
        if (_sceneUi is not null)
        {
            return _sceneUi.Root.Children;
        }

        return _stage!.Root.Children.Values;
    }

    private static bool SwfUiForced() =>
        System.Environment.GetEnvironmentVariable("REFII_KEYSTONE_SWF_UI") is "1" or "true";

    private List<string> ApplySettings(PubGamesSet set, PubGameEntry game)
    {
        PubGameSettings? shared = File.Exists(set.SettingsPath)
            ? PubGamesText.ParseSettings(File.ReadAllBytes(set.SettingsPath), out _)
            : null;

        string ownPath = Path.Combine(game.Folder, PubGamesCatalog.SettingsFileName);
        PubGameSettings? own = File.Exists(ownPath)
            ? PubGamesText.ParseSettings(File.ReadAllBytes(ownPath), out _)
            : null;

        string wanted = System.Environment.GetEnvironmentVariable("REFII_KEYSTONE_VARIANT")
            ?? string.Empty;
        _keystone.VariantName = wanted.Length != 0 ? wanted : Variant;
        List<string> applied = KeystoneConfig.Apply(_keystone, shared, own);

        if (AiPlayers != Unset)
        {
            _keystone.NumberOfAIPlayers = AiPlayers;
            applied.Add($"override numberOfAIPlayers={AiPlayers}");
        }

        if (Skin != Unset)
        {
            _keystone.SkinIndex = Skin;
            applied.Add($"override skinIndex={Skin}");
        }

        if (BuyIn != Unset)
        {
            _keystone.BuyIn = BuyIn;
            applied.Add($"override buyIn={BuyIn}");
        }

        return applied;
    }

    /// <summary>
    /// Says so, loudly, when the table the guest named is not in the file.
    /// </summary>
    /// <remarks>
    /// A VARIATION THAT DOES NOT RESOLVE IS A TABLE WITH THE WRONG STAKES, not a missing label.
    /// See <see cref="PubGameConfig.VariantMissing"/> — the guest hands the name over verbatim,
    /// and what it hands over is whatever the village's GDB holds.
    /// </remarks>
    private static void WarnIfTableMissing(List<string> applied)
    {
        foreach (string entry in applied)
        {
            if (entry.EndsWith(PubGameConfig.VariantMissing, StringComparison.Ordinal))
            {
                GD.PushWarning(
                    $"[pubgame] {entry[..^PubGameConfig.VariantMissing.Length]} is not a table in " +
                    "this game's settings.xml; playing the file's defaults instead");
            }
        }
    }

    private ulong ResolveSeed() =>
        Seed != 0 ? Seed : (ulong)System.DateTime.UtcNow.Ticks;

    private string ResolveRoot()
    {
        if (Path.IsPathRooted(GameRoot) && PubGamesCatalog.ResolveRoot(GameRoot).Length != 0)
        {
            return GameRoot;
        }

        string shared = Engine_GameData.ResolveWhere(
            candidate => PubGamesCatalog.ResolveRoot(candidate).Length != 0);
        if (shared.Length != 0)
        {
            return shared;
        }

        string fromExe = Path.Combine(
            Path.GetDirectoryName(OS.GetExecutablePath()) ?? string.Empty, GameRoot);
        return Directory.Exists(fromExe) ? fromExe : GameRoot;
    }

    private void LoadLocalization(PubGamesSet set, PubGameEntry game)
    {
        var paths = new List<string>();
        if (set.SharedLocalization.TryGetValue(Language, out string? shared))
        {
            paths.Add(shared);
        }

        if (game.Localization.TryGetValue(Language, out string? own))
        {
            paths.Add(own);
        }

        foreach (string path in paths.Where(File.Exists))
        {
            Dictionary<string, string>? map =
                PubGamesText.ParseLocalization(File.ReadAllBytes(path), out _);
            if (map is null)
            {
                continue;
            }

            foreach ((string id, string text) in map)
            {
                _keystone.Localizer.SetString(id, text);
            }
        }
    }

    public override void _Process(double delta)
    {
        if (!_ready)
        {
            return;
        }

        long started = System.Diagnostics.Stopwatch.GetTimestamp();

        if (_autoOpenBank)
        {
            _autoOpenBank = false;
            _overlays.OpenBank();
        }

        _driver.Advance(_keystone, delta, this);
        GamePad pad = _keystone.Pad ?? _keystone.Input.GetKeyboardPadState();

        AdvanceUi(delta);
        Fit();
        _scene?.Sync();
        _processMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        Report(delta, pad);
        _shot.Tick(this);
    }

    private double _processMs;

    private readonly PubGameShot _shot = new();

    private readonly PubGameDriver _driver = new();

    public bool IsFinished => _ready && _commands.GameOverIssued;

    public bool ReloadRequested => _ready && _commands.ReloadRequested;

    public bool TutorialRequested => _ready && _commands.TutorialRequested;

    public bool Failed => _failed;

    PubGame? IPubGameSession.Game => _keystone;

    public Keystone Game => _keystone;

    public GameBoard Board => _board;

    public const float MaxRasterScale = 3f;

    private float StageFit() =>
        PubGameStageFit.Scale(GetViewport().GetVisibleRect().Size, _stageSize);

    private float RasterScale()
    {
        float setting = Engine_TexSettings.PubGameRasterScale;
        float wanted = setting > 0.0f
            ? setting
            : Mathf.Ceil(StageFit() * 2f) / 2f;

        return Mathf.Clamp(wanted, 1f, MaxRasterScale);
    }

    private void Fit()
    {
        Vector2 viewport = GetViewport().GetVisibleRect().Size;
        float scale = StageFit();
        PubGameStageFit.Apply(_view, viewport, _stageSize);

        if (_scene is null || _scene.Vector)
        {
            return;
        }

        _scene.TextureScale = RasterScale();

        if (!Mathf.IsEqualApprox(_scene.TextureScale, _reportedRaster))
        {
            _reportedRaster = _scene.TextureScale;
            GD.Print(
                $"[pubgame] viewport {viewport.X:0}x{viewport.Y:0}, stage fit {scale:0.00}x, " +
                $"rasterizing at {_scene.TextureScale:0.0}x");
        }
    }

    private float _reportedRaster;

    private void Report(double delta, GamePad pad)
    {
        if (!ShowDiagnostics)
        {
            return;
        }

        _fpsAccumulator += delta;
        _fpsFrames++;
        if (_fpsAccumulator >= 1.0)
        {
            _fps = _fpsFrames / _fpsAccumulator;
            _fpsAccumulator = 0;
            _fpsFrames = 0;

            if (_sceneUi is not null &&
                System.Environment.GetEnvironmentVariable("REFII_KEYSTONE_CLIP_LOG") is "1")
            {
                int nodes = 0, players = 0, playing = 0, meshes = 0;
                void Walk(Node n)
                {
                    nodes++;
                    if (n is AnimationPlayer ap) { players++; if (ap.IsPlaying()) playing++; }
                    if (n is MeshInstance2D) meshes++;
                    foreach (Node c in n.GetChildren()) Walk(c);
                }

                Walk(_sceneUi.Node);
                GD.Print($"[perf] fps {_fps:F1} nodes {nodes} meshes {meshes} players {players} playing {playing} " +
                         $"process {_processMs:F2} ms  frame {Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000:F1} ms  " +
                         $"draw calls {Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame)}  " +
                         $"objects {Performance.GetMonitor(Performance.Monitor.RenderTotalObjectsInFrame)}");
            }
        }

        string device = Godot.Input.GetConnectedJoypads().Count > 0
            ? Godot.Input.GetJoyName(0)
            : "keyboard";

        string held =
            (pad.BtnA ? "A" : "-") + (pad.BtnB ? "B" : "-") +
            (pad.BtnX ? "X" : "-") + (pad.BtnY ? "Y" : "-") +
            (pad.LeftShoulder ? " LB" : "") + (pad.RightShoulder ? " RB" : "");

        if (_overlays.AbsorbingInput)
        {
            _status.Text =
                $"{device}   {_fps:F1} fps\n" +
                (_overlays.NeedChipsShowing
                    ? "not enough chips - A visits the bank  B cancels"
                    : _overlays.HelpShowing
                    ? $"how to play {_overlays.HelpSection}/{_overlays.HelpSectionCount}" +
                      "  left/right pages  B closes"
                    : _overlays.CashOutShowing
                    ? "cash out - A confirms and ends the session"
                    : _overlays.QuitShowing
                    ? "quit? A confirms  B cancels"
                    : _overlays.StandingsShowing
                    ? "session stats - release RT or B closes"
                    : _overlays.QuickTipsShowing
                    ? "odds table - release LT or B closes"
                    : _overlays.BankSubShowing
                        ? $"left/right sets amount: {_overlays.SliderValue}   A confirms  B cancels"
                        : $"bank: {_overlays.SelectedOption}   up/down  A selects  B closes") +
                $"\nchips {_human.Chips}  gold {_human.Gold}  debt {_human.Debt}  " +
                $"credit {_overlays.Bank.GetCredit()}";
            return;
        }

        string hint = _keystone.PumpFunction?.Method.Name switch
        {
            "BetOnStones" => "bet the arch, X moves inside   RB repeats last bets",
            "BetOnInside" => "bet inside, X rolls   RB repeats last bets",
            "WaitForContinue" => "press A to collect",
            _ => "animating - A speeds it up",
        };

        _status.Text =
            $"{device}   {_fps:F1} fps\n" +
            $"space {KeystoneRules.BetIdToString(_keystone.Current)}  " +
            $"pays {_keystone.GetPayoutRate(_keystone.Current)}:" +
            $"{_keystone.GetPayoutBet(_keystone.Current)}\n" +
            $"bet {_human.Bets[_keystone.Current]}   chips {_human.Chips}   " +
            $"staked {_human.GetTotalBetAmount()}\n" +
            $"game {_keystone.CurrentGame} roll {_keystone.CurrentRoll}   " +
            $"last {_keystone.Roll1}+{_keystone.Roll2}+{_keystone.Roll3}=" +
            $"{_keystone.RollTotal}\n" +
            $"buttons [{held}]   stick ({pad.ThumbLX:F2},{pad.ThumbLY:F2})\n" +
            $"{hint}\n" +
            (_scene is not null ? $"nodes {_scene.DrawnNodes}" : "scene ui");
    }
}
