using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Godot;

using Refii.Core;
using Refii.FlashGames.Framework;
using Refii.Textures;
using Refii.Ui.Flash;

namespace Refii.FlashGames.MageDice;

public partial class MageDiceHost : Node, IPubGameSession
{
    public const string GameName = "Magedice";

    public const int Unset = -1;

    [Export]
    public string GameRoot { get; set; } = "assets/game";

    [Export]
    public string Language { get; set; } = "en";

    [Export]
    public int AiPlayers { get; set; } = Unset;

    [Export]
    public ulong Seed { get; set; }

    [Export]
    public int Skin { get; set; } = Unset;

    [Export]
    public string Variant { get; set; } = "CowAndCorset1";

    [Export]
    public int BuyIn { get; set; } = Unset;

    [Export]
    public int StartingGold { get; set; } = 2000;

    [Export]
    public bool ShowDiagnostics { get; set; } = true;

    private MageDice _game = null!;
    private Player _human = null!;
    private Node2D _view = null!;
    private Label _status = null!;
    private PubStopwatchTimer _timer = null!;
    private PubGameCommandSink _commands = null!;
    private MageDiceOverlayManager _overlays = null!;

    private SwfSceneUi? _sceneUi;
    private IPubClip _root = null!;
    private Vector2 _stageSize = new(640f, 462f);

    private double _fpsAccumulator;
    private int _fpsFrames;
    private double _fps;
    private bool _ready;

    public MageDice Game => _game;

    public SwfSceneUi? SceneUi => _sceneUi;

    public override void _Ready()
    {
        Engine_TexEnhance.EnsureRegistered();

        _view = new Node2D();
        AddChild(_view);

        _status = new Label { Position = new Vector2(8, 8), Visible = ShowDiagnostics };
        AddChild(_status);

        if (!Load())
        {
            string message = "mage dice not found under " + GameRoot;
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
        PubGameEntry? game = set?.Games.FirstOrDefault(g => g.Name.Contains("magedice"));
        if (set is null || game is null)
        {
            return false;
        }

        if (!LoadSceneUi(root))
        {
            return false;
        }

        _timer = new PubStopwatchTimer();
        var random = new PubRandom(ResolveSeed());
        _commands = new PubGameCommandSink();
        _game = new MageDice(_commands, _timer, random, _root);

        LoadLocalization(set, game);
        List<string> applied = ApplySettings(set, game);
        GD.Print($"[pubgame] settings: {string.Join(", ", applied)}");

        _human = _game.CreatePlayerObject(0, false, "Hero", StartingGold, 0, 1, 0, 0);
        _game.PlayerInfo.AddPlayer(0, _human);

        _game.Initialize();

        _overlays = new MageDiceOverlayManager(_game, _root);
        _game.OverlayManager = _overlays;

        _game.Run();

        if (System.Environment.GetEnvironmentVariable("REFII_MAGEDICE_AUTOPLAY") is "1")
        {
            _game.AutoPlay = true;
            GD.Print("[pubgame] autoplay on (REFII_MAGEDICE_AUTOPLAY)");
        }

        string force = System.Environment.GetEnvironmentVariable("REFII_MAGEDICE_FORCE")
            ?? string.Empty;
        if (force.Length != 0)
        {
            var values = new List<int>();
            foreach (string part in force.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(part.Trim(), out int value))
                {
                    values.Add(value);
                }
            }

            _game.ForceReels(values);
            GD.Print($"[pubgame] forcing reels [{string.Join(" ", values)}]");
        }

        GD.Print(
            $"[pubgame] {game.Name} from {set.Root}: scene {_sceneUi!.ScenePath}, " +
            $"{_stageSize.X:0}x{_stageSize.Y:0} stage, variant '{_game.VariantName}', " +
            $"{_game.Variant.GameType} reels");

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
        GD.Print(
            $"[pubgame] scene ui: mount {mounted:0} ms, load+instantiate {loaded - mounted:0} ms, " +
            $"in tree {clock.Elapsed.TotalMilliseconds - loaded:0} ms");
        return true;
    }

    private List<string> ApplySettings(PubGamesSet set, PubGameEntry game)
    {
        PubGameSettings? shared = File.Exists(set.SettingsPath)
            ? PubGamesText.ParseSettings(File.ReadAllBytes(set.SettingsPath), out _)
            : null;

        string ownPath = Path.Combine(game.Folder, PubGamesCatalog.SettingsFileName);
        PubGameSettings? own = File.Exists(ownPath)
            ? PubGamesText.ParseSettings(File.ReadAllBytes(ownPath), out _)
            : null;

        string wanted = System.Environment.GetEnvironmentVariable("REFII_MAGEDICE_VARIANT")
            ?? string.Empty;
        _game.VariantName = wanted.Length != 0 ? wanted : Variant;
        List<string> applied = MageDiceConfig.Apply(_game, shared, own);

        if (AiPlayers != Unset)
        {
            _game.NumberOfAIPlayers = AiPlayers;
            applied.Add($"override numberOfAIPlayers={AiPlayers}");
        }

        if (Skin != Unset)
        {
            _game.SkinIndex = Skin;
            applied.Add($"override skinIndex={Skin}");
        }

        if (BuyIn != Unset)
        {
            _game.BuyIn = BuyIn;
            applied.Add($"override buyIn={BuyIn}");
        }

        return applied;
    }

    private ulong ResolveSeed() => Seed != 0 ? Seed : (ulong)DateTime.UtcNow.Ticks;

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
                _game.Localizer.SetString(id, text);
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

        PubGameInput.Poll(_game.Input);
        _game.Input.Update();
        GamePad pad = _game.Input.GetPadState(0) ?? _game.Input.GetKeyboardPadState();
        _game.SetPad(pad);

        _overlays.Update(ticks, pad);

        if (!_overlays.AbsorbingInput)
        {
            if (pad.BackPressed)
            {
                _game.EndGame();
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
        }

        _game.CallbackManager.DoCallbacks(ticks);

        _sceneUi?.Advance(delta);
        Fit();
        Report(delta, pad);
        _shot.Tick(this);
    }

    private readonly PubGameShot _shot = new();

    private void Fit()
    {
        Vector2 viewport = GetViewport().GetVisibleRect().Size;
        float width = Mathf.Max(_stageSize.X, 1f);
        float height = Mathf.Max(_stageSize.Y, 1f);
        float scale = Mathf.Min(viewport.X / width, viewport.Y / height);

        _view.Scale = new Vector2(scale, scale);
        _view.Position = new Vector2(
            (viewport.X - (width * scale)) * 0.5f,
            (viewport.Y - (height * scale)) * 0.5f);
    }

    public bool IsFinished => _ready && _commands.GameOverIssued;

    public long FinalGold => _human?.Gold ?? 0;

    public long FinalDebt => _human?.Debt ?? 0;

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

            if (System.Environment.GetEnvironmentVariable("REFII_MAGEDICE_LOG") is "1")
            {
                GameBoard live = _game.GetBoard(0);
                var faces = new List<string>();
                for (var i = 0; i < _game.Variant.GameType; i++)
                {
                    int v = live.GetSwitch(i).Value;
                    faces.Add(v >= 0 && v < _game.Variant.ReelImages.Count
                        ? _game.Variant.ReelImages[v]
                        : "-");
                }

                GD.Print(
                    $"[magedice] spin {_game.TotalSpins} [{string.Join(" ", faces)}] " +
                    $"bet {live.CostPerSpin()} chips {_human.Chips} " +
                    $"won {live.Paid} chains {live.Chains.Count} " +
                    $"free {live.FreeSpins}/{live.TotalFreeSpins} mult x{live.BonusMultiplier}");
            }
        }

        string device = Godot.Input.GetConnectedJoypads().Count > 0
            ? Godot.Input.GetJoyName(0)
            : "keyboard";

        if (_overlays.AbsorbingInput)
        {
            _status.Text =
                $"{device}   {_fps:F1} fps\n" +
                (_overlays.NeedChipsShowing ? "not enough chips - A visits the bank  B cancels"
                    : _overlays.HelpShowing ? "how to play  left/right pages  B closes"
                    : _overlays.CashOutShowing ? "cash out - A confirms and ends the session"
                    : _overlays.QuitShowing ? "quit? A confirms  B cancels"
                    : _overlays.StandingsShowing ? "session stats - release RT or B closes"
                    : _overlays.QuickTipsShowing ? "paytable - release LT or B closes"
                    : _overlays.BankSubShowing
                        ? $"left/right sets amount: {_overlays.SliderValue}   A confirms  B cancels"
                        : $"bank: {_overlays.SelectedOption}   up/down  A selects  B closes") +
                $"\nchips {_human.Chips}  gold {_human.Gold}  debt {_human.Debt}";
            return;
        }

        GameBoard board = _game.GetBoard(0);
        var reels = new List<string>();
        for (var i = 0; i < _game.Variant.GameType; i++)
        {
            int value = board.GetSwitch(i).Value;
            reels.Add(value >= 0 && value < _game.Variant.ReelImages.Count
                ? _game.Variant.ReelImages[value]
                : "-");
        }

        _status.Text =
            $"{device}   {_fps:F1} fps\n" +
            $"{_game.VariantName}  {_game.Variant.GameType} reels  skin {_game.SkinIndex}\n" +
            $"bet {board.CostPerSpin()} (x{board.BetMultiple})   chips {_human.Chips}\n" +
            $"reels [{string.Join(" ", reels)}]\n" +
            $"spin {_game.TotalSpins}  won {board.RunningPayout}  " +
            $"free {board.FreeSpins}/{board.TotalFreeSpins}  mult x{board.BonusMultiplier}\n" +
            "A spins   left/right adjusts bet   Y bank   LT paytable   RT stats";
    }
}
