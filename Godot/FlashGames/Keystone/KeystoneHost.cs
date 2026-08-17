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
    private bool _rolling;
    private Phase _phase = Phase.ArchBetting;

    private enum Phase
    {
        ArchBetting,
        RevealInside,
        InsideBetting,
        Rolling,
        Sweep,
        Payouts,
        AwaitContinue,
        Collect,
    }

    public SwfStage? Stage => _stage;

    public SwfScene? Scene => _scene;

    public SwfSceneUi? SceneUi => _sceneUi;

    public KeystoneOverlayManager Overlays => _overlays;

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
        _commands = new PubGameCommandSink();
        _keystone = new Keystone(_commands, _timer, random);

        LoadLocalization(set, game);
        List<string> applied = ApplySettings(set, game);
        GD.Print($"[pubgame] settings: {string.Join(", ", applied)}");

        if (SwfUiForced() || !LoadSceneUi(root))
        {
            if (!LoadSwfStage(set, game))
            {
                return false;
            }
        }

        _keystone.InitBetLimits();
        _keystone.InitBoardInfos();
        _keystone.ResetArchStones();

        IPubClip? mark1 = _root.GetChild("player0chips1");
        IPubClip? mark2 = _root.GetChild("player0chips2");
        if (mark1 is not null && mark2 is not null)
        {
            _keystone.WinNumbers = new AnimateWinNumbers(
                (mark1.X + mark2.X) * 0.5f, mark2.Y, _keystone.ChipTravelTime);
        }

        var boardClock = System.Diagnostics.Stopwatch.StartNew();
        _board = new GameBoard(_keystone, _root, random);
        _board.Init();
        GD.Print($"[pubgame] board init {boardClock.Elapsed.TotalMilliseconds:0} ms");
        _keystone.SeatsPerTable =
            Math.Min(1 + _keystone.NumberOfAIPlayers, _board.MaxPlayersPerBoard);
        _board.AdjustLayers(false);
        _board.InitCenter();
        _board.InitNotice();
        _board.InitSpaceStats();
        _board.InitBetStats();
        _board.InitTourneyStats();
        for (int i = 1; i <= 5; i++)
        {
            _board.InitLegend(i);
        }

        _keystone.Board = _board;

        foreach (IPubClip child in RootChildren())
        {
            bool variant = child.Name.EndsWith("SD", StringComparison.Ordinal) ||
                child.Name.EndsWith("WD", StringComparison.Ordinal);
            if (variant || child.Name is "jackpot")
            {
                child.Visible = false;
            }
        }

        _human = new KeystonePlayer(0, false, "Hero", StartingGold, 0, 1, 0, 0)
        {
            Chips = _keystone.BuyIn,
        };
        _keystone.PlayerInfo.AddPlayer(0, _human);
        _keystone.BoardInfos[0].SetPlayer(0, _human);
        _keystone.SeatAiPlayers();
        _keystone.InitPlayers();
        _board.HideUnusedPlayerUI(_keystone.SeatsPerTable);

        _keystone.RevealBoard();

        IPubClip? betStats = _root.GetChild("betStats");
        if (betStats is not null)
        {
            betStats.Alpha = 100;
        }

        IPubClip? spaceStats = _root.GetChild("spaceStats");
        if (spaceStats is not null)
        {
            spaceStats.Alpha = 100;
        }

        foreach (IPubClip child in RootChildren())
        {
            if (child.GetDepth() == UnresolvedLightingDepth)
            {
                child.Visible = false;
            }
        }

        _board.RevealStones(null);
        _keystone.JumpToNewLocation(KeystoneSpace.Out10, false, null);
        _keystone.UpdateBetStats();

        IPubClip? ticksField = _root.GetChild("ticks");
        if (ticksField is not null)
        {
            ticksField.Visible = false;
        }

        _overlays = new KeystoneOverlayManager(_keystone, _root);
        _keystone.OverlayManager = _overlays;

        if (_keystone.Tutorial)
        {
            _keystone.TutorialPreFadeIn();
            _keystone.EnterTutorialStage();
        }
        else
        {
            _keystone.StartArchBetting();
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

        double ticks = delta * 1000.0;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();

        if (_autoOpenBank)
        {
            _autoOpenBank = false;
            _overlays.OpenBank();
        }

        PubGameInput.Poll(_keystone.Input);
        _keystone.Input.Update();
        GamePad pad = _keystone.Input.GetPadState(0) ?? _keystone.Input.GetKeyboardPadState();

        _overlays.Update(ticks, pad);

        if (_overlays.AbsorbingInput)
        {
            _keystone.CallbackManager.DoCallbacks(ticks);
            _keystone.WinNumbers?.Update(ticks);
            AdvanceUi(delta);
            Fit();
            _scene?.Sync();
            Report(delta, pad);
            return;
        }

        if (pad.BackPressed)
        {
            _keystone.EndGame();
        }
        else if (pad.StartPressed)
        {
            _overlays.ShowHelp();
        }
        else if (pad.BtnYPressed)
        {
            _overlays.OpenBank();
        }
        else if (pad.LeftTriggerPressed)
        {
            _overlays.ToggleQuickTips();
        }
        else if (pad.RightTriggerPressed)
        {
            _overlays.ShowStandings(true);
        }

        if (_keystone.Tutorial)
        {
            _keystone.HandleTutorialInput(pad, ticks);
            _keystone.CallbackManager.DoCallbacks(ticks);
            _keystone.UpdatePlayerChipDisplays(ticks);
            _keystone.WinNumbers?.Update(ticks);
            AdvanceUi(delta);
            SyncChips();
            Fit();
            _scene?.Sync();
            Report(delta, pad);
            return;
        }

        if (_phase == Phase.ArchBetting)
        {
            if (_keystone.BetPhase(true, ticks, pad))
            {
                _keystone.EndBettingPhase(true);
                _board.ShowCenter(true, 250, false);
                _phase = Phase.RevealInside;
            }

            SyncAiStacks();
        }
        else if (_phase == Phase.InsideBetting)
        {
            if (_keystone.BetPhase(false, ticks, pad))
            {
                _keystone.EndBettingPhase(false);
                Roll();
            }

            SyncAiStacks();
        }
        else if (_phase == Phase.AwaitContinue && pad.BtnAPressed)
        {
            _board.PulseLegend(2);
            _keystone.ShowContinueGameText(false);
            _keystone.AddAnimating(_keystone.MoveWinsToPlayers(null));
            _phase = Phase.Collect;
        }

        _keystone.CallbackManager.DoCallbacks(ticks);
        _keystone.UpdatePlayerChipDisplays(ticks);
        _keystone.WinNumbers?.Update(ticks);
        AdvanceUi(delta);

        if (_keystone.CallbackManager.Count == 0)
        {
            Settle();
        }

        SyncChips();
        Fit();
        _scene?.Sync();
        _processMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        Report(delta, pad);
        _shot.Tick(this);
    }

    private double _processMs;

    private readonly PubGameShot _shot = new();

    private void Settle()
    {
        switch (_phase)
        {
            case Phase.RevealInside:
                _keystone.StartInsideBetting();
                _phase = Phase.InsideBetting;
                break;
            case Phase.Rolling:
                Resolve();
                break;
            case Phase.Sweep:
                _keystone.AddAnimating(_keystone.ShowPayoutStacks(null));
                _keystone.UpdateBetStats();
                _phase = Phase.Payouts;
                break;
            case Phase.Payouts:
                _keystone.ShowContinueGameText(true);
                _keystone.ReportPointsEarned();
                _phase = Phase.AwaitContinue;
                break;
            case Phase.Collect:
                NextRound();
                break;
        }
    }

    private void Resolve()
    {
        if (_keystone.JackpotRolled)
        {
            _board.ShowJackpot(false, null);
            if (!_keystone.Tutorial)
            {
                _keystone.AchievementEarned("KS_Jackpot");
            }
        }

        _keystone.GenerateAndResolvePayouts(_keystone.BoardInfos[0]);
        _board.RemoveStone(_keystone.ArchStoneToRemove, null);
        _keystone.RemoveArchStone();
        _board.ShowNumberAbove(_keystone.ArchStoneToRemove, false, null, false, 0);
        _keystone.AddAnimating(_keystone.AnimateLostBetRemoval(false, null));
        _keystone.AddAnimating(_keystone.AnimateLostBetRemoval(true, null));
        _phase = Phase.Sweep;
    }

    private void NextRound()
    {
        _board.ClearAllHilights();
        _rolling = false;

        if (_keystone.IsGameOver())
        {
            _board.CollapseArch(null);
            _keystone.DelayCallback(2000, _ =>
            {
                _keystone.ResetArchStones();
                for (int i = KeystoneSpace.Out3; i <= KeystoneSpace.Out18; i++)
                {
                    _board.SetStoneOffScreen(i);
                }

                _board.RevealStones(null);
                _board.ShowCenter(false, 250, false);
                _keystone.StartNextGame();
                _keystone.StartArchBetting();
            });

            _phase = Phase.ArchBetting;
            return;
        }

        _keystone.AdvanceRoll();
        _board.ShowCenter(false, 250, false);
        _keystone.StartArchBetting();
        _phase = Phase.ArchBetting;
    }

    public bool IsFinished => _ready && _commands.GameOverIssued;

    public long FinalGold => _human?.Gold ?? 0;

    public long FinalDebt => _human?.Debt ?? 0;

    public bool IsRolling => _rolling;

    public string PhaseName => _phase.ToString();

    public void Advance(bool arch)
    {
        _keystone.EndBettingPhase(arch);
        if (arch)
        {
            _board.ShowCenter(true, 250, false);
            _phase = Phase.RevealInside;
            return;
        }

        Roll();
    }

    public void Collect()
    {
        _keystone.AddAnimating(_keystone.MoveWinsToPlayers(null));
        _phase = Phase.Collect;
    }

    public Keystone Game => _keystone;

    public GameBoard Board => _board;

    public void Roll()
    {
        _rolling = true;
        _phase = Phase.Rolling;
        _keystone.GenerateRoll();
        _board.RollDice(_keystone.Roll1, _keystone.Roll2, _keystone.Roll3, false, null);
        _keystone.DelayCallback(2600, _ => _board.PresentDice(500, null));
        _keystone.DelayCallback(
            3400, _ => _board.ShowNumberAbove(_keystone.ArchStoneToRemove, true, null, false, 0));
    }

    private void SyncAiStacks()
    {
        BoardInfo info = _keystone.BoardInfos[0];
        for (int seat = 1; seat < _keystone.SeatsPerTable; seat++)
        {
            KeystonePlayer? player = info.GetPlayer(seat);
            ChipStackMulti? total = _board.GetPlayerStack(seat);
            if (player is null || total is null)
            {
                continue;
            }

            if (total.Value != player.Chips)
            {
                total.Value = player.Chips;
            }

            for (int space = KeystoneSpace.Out3; space <= KeystoneSpace.Out18; space++)
            {
                if (player.Bets[space] <= 0)
                {
                    continue;
                }

                ChipStack? stack = _board.GetChipStack(seat, space);
                if (stack is not null && stack.Value != player.Bets[space])
                {
                    stack.Value = player.Bets[space];
                }
            }
        }
    }

    private void SyncChips()
    {
        ChipStackMulti? stack = _board.GetPlayerStack(0);
        if (stack is not null && stack.Value != _human.Chips)
        {
            stack.Value = _human.Chips;
        }

        ChipStack? space = _board.GetChipStack(0, _keystone.Current);
        if (space is not null && space.Value != _human.Bets[_keystone.Current])
        {
            space.Value = _human.Bets[_keystone.Current];
        }
    }

    public const float MaxRasterScale = 3f;

    private float StageWidth => Mathf.Max(_stageSize.X, 1f);

    private float StageHeight => Mathf.Max(_stageSize.Y, 1f);

    private float StageFit()
    {
        Vector2 viewport = GetViewport().GetVisibleRect().Size;
        return Mathf.Min(viewport.X / StageWidth, viewport.Y / StageHeight);
    }

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

        _view.Scale = new Vector2(scale, scale);
        _view.Position = new Vector2(
            (viewport.X - (StageWidth * scale)) * 0.5f,
            (viewport.Y - (StageHeight * scale)) * 0.5f);

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

        string hint = _phase switch
        {
            Phase.ArchBetting => _human.GetBetCount(true) > 0
                ? "X moves to inside betting   RB repeats last bets"
                : "bet the arch with A   RB repeats last bets",
            Phase.RevealInside => "revealing inner board",
            Phase.InsideBetting => "bet inside, X to roll   RB repeats last bets",
            Phase.Rolling => "rolling...",
            Phase.Sweep => "clearing losing bets",
            Phase.Payouts => "paying out",
            Phase.AwaitContinue => "press A to collect",
            Phase.Collect => "collecting",
            _ => "betting",
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
