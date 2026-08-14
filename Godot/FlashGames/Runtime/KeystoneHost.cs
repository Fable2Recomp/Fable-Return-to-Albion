using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Refii.FlashGames.PubGames;

namespace Refii.FlashGames.Runtime;

public partial class KeystoneHost : Node
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
    public int AiPlayers { get; set; } = 3;

    [Export]
    public ulong Seed { get; set; } = 20260814;

    [Export]
    public int Skin { get; set; }

    [Export]
    public string Variant { get; set; } = string.Empty;

    [Export]
    public int BuyIn { get; set; } = 500;

    [Export]
    public int StartingGold { get; set; } = 2000;

    public const int UnresolvedLightingDepth = 3;

    private SwfStage _stage = null!;
    private Keystone _keystone = null!;
    private GameBoard _board = null!;
    private KeystonePlayer _human = null!;
    private Node2D _view = null!;
    private SwfScene _scene = null!;
    private PubOverlays _overlays = null!;
    private Label _status = null!;
    private PubStopwatchTimer _timer = null!;

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

    public SwfStage Stage => _stage;

    public SwfScene Scene => _scene;

    public PubOverlays Overlays => _overlays;

    public override void _Ready()
    {
        _view = new Node2D();
        AddChild(_view);

        _status = new Label { Position = new Vector2(8, 8) };
        AddChild(_status);

        if (!Load())
        {
            _status.Text = "pub games not found under " + GameRoot;
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

        SwfFile? file = SwfFile.TryLoad(File.ReadAllBytes(game.SwfPath), out string error);
        if (file is null)
        {
            _status.Text = "swf load failed: " + error;
            return false;
        }

        SwfDocument document = SwfDocument.Build(file);
        PubGameTextures textures = PubGameTextures.Build(set, game, document, Skin);
        set.SharedFonts.TryGetValue(Language, out string? pack);
        PubGameFonts fonts = PubGameFonts.Build(document, pack ?? string.Empty, Language);

        _stage = new SwfStage(document, file.FrameSize, textures.Resolve) { Fonts = fonts };
        _timer = new PubStopwatchTimer();

        var random = new PubRandom(Seed);
        _keystone = new Keystone(new PubGameHostLog(), _timer, random)
        {
            NumberOfAIPlayers = AiPlayers,
            SkinIndex = Skin,
        };

        LoadLocalization(set, game);
        ApplySettings(set, game);

        _keystone.InitBetLimits();
        _keystone.InitBoardInfos();
        _keystone.ResetArchStones();

        IPubClip? mark1 = _stage.Root.GetChild("player0chips1");
        IPubClip? mark2 = _stage.Root.GetChild("player0chips2");
        if (mark1 is not null && mark2 is not null)
        {
            _keystone.WinNumbers = new AnimateWinNumbers(
                (mark1.X + mark2.X) * 0.5f, mark2.Y, _keystone.ChipTravelTime);
        }

        _board = new GameBoard(_keystone, _stage.Root, random);
        _board.Init();
        _keystone.SeatsPerTable = Math.Min(1 + AiPlayers, _board.MaxPlayersPerBoard);
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

        foreach (SwfClip child in _stage.Root.Children.Values)
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

        _stage.Root.GetChild("betStats")!.Alpha = 100;
        _stage.Root.GetChild("spaceStats")!.Alpha = 100;

        foreach (SwfClip child in _stage.Root.Children.Values)
        {
            if (child.Depth == UnresolvedLightingDepth)
            {
                child.Visible = false;
            }
        }

        _board.RevealStones(null);
        _keystone.JumpToNewLocation(KeystoneSpace.Out10, false, null);
        _keystone.UpdateBetStats();
        _keystone.StartArchBetting();

        IPubClip? ticksField = _stage.Root.GetChild("ticks");
        if (ticksField is not null)
        {
            ticksField.Visible = false;
        }

        _overlays = new PubOverlays(_keystone, _stage.Root);
        _keystone.OverlayManager = _overlays;

        _scene = new SwfScene(_stage, _view);
        _scene.Sync();

        return true;
    }

    private void ApplySettings(PubGamesSet set, PubGameEntry game)
    {
        PubGameSettings? shared = File.Exists(set.SettingsPath)
            ? PubGamesText.ParseSettings(File.ReadAllBytes(set.SettingsPath), out _)
            : null;

        string ownPath = Path.Combine(game.Folder, PubGamesCatalog.SettingsFileName);
        PubGameSettings? own = File.Exists(ownPath)
            ? PubGamesText.ParseSettings(File.ReadAllBytes(ownPath), out _)
            : null;

        _keystone.VariantName = Variant;
        PubGameConfig.Apply(_keystone, shared, own);

        if (AiPlayers > 0)
        {
            _keystone.NumberOfAIPlayers = AiPlayers;
        }

        if (Skin > 0)
        {
            _keystone.SkinIndex = Skin;
        }

        if (BuyIn > 0)
        {
            _keystone.BuyIn = BuyIn;
        }
    }

    private string ResolveRoot()
    {
        if (Path.IsPathRooted(GameRoot) && Directory.Exists(GameRoot))
        {
            return GameRoot;
        }

        string fromProject = ProjectSettings.GlobalizePath("res://../" + GameRoot);
        if (Directory.Exists(fromProject))
        {
            return fromProject;
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

        PubGameInput.Poll(_keystone.Input);
        _keystone.Input.Update();
        GamePad pad = _keystone.Input.GetPadState(0) ?? _keystone.Input.GetKeyboardPadState();

        _overlays.Update(ticks, pad);

        if (_overlays.AbsorbingInput)
        {
            _keystone.CallbackManager.DoCallbacks(ticks);
            _keystone.WinNumbers?.Update(ticks);
            _stage.Advance();
            Fit();
            _scene.Sync();
            Report(delta, pad);
            return;
        }

        if (pad.BackPressed)
        {
            _overlays.ShowQuit();
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
        _stage.Advance();

        if (_keystone.CallbackManager.Count == 0)
        {
            Settle();
        }

        SyncChips();
        Fit();
        _scene.Sync();
        Report(delta, pad);
    }

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

    private void Fit()
    {
        Vector2 viewport = GetViewport().GetVisibleRect().Size;
        float stageWidth = Mathf.Max(_stage.FrameSize.Width, 1) / SwfShapeGeometry.TwipsPerPixel;
        float stageHeight = Mathf.Max(_stage.FrameSize.Height, 1) / SwfShapeGeometry.TwipsPerPixel;
        float scale = Mathf.Min(viewport.X / stageWidth, viewport.Y / stageHeight);

        _view.Scale = new Vector2(scale, scale);
        _view.Position = new Vector2(
            (viewport.X - (stageWidth * scale)) * 0.5f,
            (viewport.Y - (stageHeight * scale)) * 0.5f);
    }

    private void Report(double delta, GamePad pad)
    {
        _fpsAccumulator += delta;
        _fpsFrames++;
        if (_fpsAccumulator >= 1.0)
        {
            _fps = _fpsFrames / _fpsAccumulator;
            _fpsAccumulator = 0;
            _fpsFrames = 0;
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
            $"pays {KeystoneRules.GetPayoutRate(_keystone.Current)}:" +
            $"{_keystone.GetPayoutBet(_keystone.Current)}\n" +
            $"bet {_human.Bets[_keystone.Current]}   chips {_human.Chips}   " +
            $"staked {_human.GetTotalBetAmount()}\n" +
            $"game {_keystone.CurrentGame} roll {_keystone.CurrentRoll}   " +
            $"last {_keystone.Roll1}+{_keystone.Roll2}+{_keystone.Roll3}=" +
            $"{_keystone.RollTotal}\n" +
            $"buttons [{held}]   stick ({pad.ThumbLX:F2},{pad.ThumbLY:F2})\n" +
            $"{hint}\n" +
            $"nodes {_scene.DrawnNodes}";
    }
}
