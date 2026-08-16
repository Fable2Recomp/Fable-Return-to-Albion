using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Refii.Core;
using Refii.Ui.Flash.Keystone;
using Refii.FlashGames.PubGames;
using Refii.Textures;

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

    /// <summary>
    /// The number of seats to fill, or -1 to take the table's own.
    /// </summary>
    /// <remarks>
    /// UNSET IS -1, NOT ZERO, AND THAT MATTERS. These four exports exist so the scene can be run on
    /// its own as a harness, but under the guest the authority is the variation named in
    /// <c>settings.xml</c> — Standard1 buys in for 100, Tutorial for 0. Defaulting them to real
    /// numbers made the harness values win every time: every pub table in the game was played with
    /// three AI opponents, a 500 buy-in and skin 0, whatever the file said. A sentinel that cannot
    /// be a legitimate setting is the only way an export can mean "I did not ask".
    /// </remarks>
    [Export]
    public int AiPlayers { get; set; } = Unset;

    /// <summary>The RNG seed, or 0 to draw a fresh one per session.</summary>
    /// <remarks>
    /// NOT A CONSTANT BY DEFAULT. A fixed seed makes every session of every table roll the same
    /// dice in the same order, which is exactly what a repeatable harness wants and exactly what a
    /// casino must not do.
    /// </remarks>
    [Export]
    public ulong Seed { get; set; }

    [Export]
    public int Skin { get; set; } = Unset;

    /// <summary>
    /// The table, by its name in the game's own <c>settings.xml</c>.
    /// </summary>
    /// <remarks>
    /// NOT EMPTY BY DEFAULT, because "no variation" is not a playable table. The file's
    /// <c>&lt;defaults&gt;</c> section states no <c>buyIn</c> at all, so a game handed no variation
    /// starts the player with nothing to bet. Standard1 is the lowest cash table and the sane
    /// thing for the scene to open on when it is run by itself; under the guest this is replaced
    /// by whichever of the 28 tables the player actually sat down at.
    /// </remarks>
    [Export]
    public string Variant { get; set; } = "Standard1";

    [Export]
    public int BuyIn { get; set; } = Unset;

    [Export]
    public int StartingGold { get; set; } = 2000;

    /// <summary>"The file decides", for the exports the guest normally supplies.</summary>
    public const int Unset = -1;

    /// <summary>
    /// Hides the on-screen diagnostic, for a host that is not a debugging harness.
    /// </summary>
    /// <remarks>
    /// The status label names the pad, the frame rate, the phase, the raw stick axes and the last
    /// roll. That is the right thing in front of a developer running the scene alone and the wrong
    /// thing in front of a player who walked into a pub.
    /// </remarks>
    [Export]
    public bool ShowDiagnostics { get; set; } = true;

    public const int UnresolvedLightingDepth = 3;

    private Keystone _keystone = null!;
    private GameBoard _board = null!;
    private KeystonePlayer _human = null!;
    private Node2D _view = null!;
    private PubOverlays _overlays = null!;
    private Label _status = null!;
    private PubStopwatchTimer _timer = null!;
    private PubGameCommandSink _commands = null!;

    private KeystoneScene? _sceneUi;
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

    public KeystoneScene? SceneUi => _sceneUi;

    public PubOverlays Overlays => _overlays;

    public override void _Ready()
    {
        // The scene is also run on its own as a harness, where nothing else has configured the
        // texture pipeline. A no-op inside a normal session, which registered at boot.
        Engine_TexEnhance.EnsureRegistered();

        _view = new Node2D();
        AddChild(_view);

        _status = new Label { Position = new Vector2(8, 8), Visible = ShowDiagnostics };
        AddChild(_status);

        if (!Load())
        {
            // ON SCREEN *AND* IN THE LOG. The label alone is invisible to a headless run, so a
            // scene that found nothing looked identical to one that loaded silently.
            string message = "pub games not found under " + GameRoot;
            _status.Text = message;

            // SHOWN EVEN WHEN DIAGNOSTICS ARE OFF. A player who sat down at a table and got a
            // blank layer is owed the reason; hiding it leaves them staring at nothing.
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

        // SETTLED BEFORE ANYTHING IS BUILT FROM IT. The table's skin comes out of the variation the
        // guest named, and the background texture is chosen by that number — building the textures
        // first means every table in the game shows background0 whatever its settings.xml says.
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

        _overlays = new PubOverlays(_keystone, _root);
        _keystone.OverlayManager = _overlays;

        // THE OVERLAYS HAVE TO EXIST FIRST. The tutorial's opening step raises a card and closes
        // the odds table off, both of which go through the overlay manager — starting it before
        // this line silently skipped its first stage.
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
                ? $"scene {KeystoneScene.ScenePath}, {_stageSize.X:0}x{_stageSize.Y:0} stage"
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
        _sceneUi = KeystoneScene.Load();
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

        // NOT SYNCED HERE, and that is the point. The viewport has no size yet during _Ready — it
        // reports zero — so the raster scale cannot be known, and rasterizing the whole board now
        // would build every shape at 1x and then throw all of it away on the first Fit(). The
        // first _Process calls Fit() and then Sync(), in that order, on both of its paths, so the
        // board is built exactly once and at the size it will be shown at.
        //
        // SAID OUT LOUD, because both halves of this have failed silently. A resolver that picked
        // an empty directory left the scene reporting "pub games not found" with the files present,
        // and a texture path that skipped the pipeline looked exactly like one that used it.
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

        _keystone.VariantName = Variant;
        List<string> applied = PubGameConfig.Apply(_keystone, shared, own);

        // THE FILE WINS UNLESS SOMEONE ASKED. See the Unset remarks above — these are harness
        // overrides, and an override only overrides when it was actually set.
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
    /// The seed the dice are drawn from.
    /// </summary>
    /// <remarks>
    /// A SET SEED IS HONOURED EXACTLY, because that is what a set seed is for — the triangulation
    /// and rules probes replay a table and compare it against the last run, and they cannot do that
    /// against a clock. Zero means nobody asked, and a session nobody seeded gets a fresh one.
    /// </remarks>
    private ulong ResolveSeed() =>
        Seed != 0 ? Seed : (ulong)System.DateTime.UtcNow.Ticks;

    /// <summary>
    /// The game-data directory that actually carries the pub games.
    /// </summary>
    /// <remarks>
    /// THE TEST IS "HAS PUB GAMES", NOT "EXISTS". Every candidate below used to be accepted on
    /// existence alone, and in this tree the first one that exists — <c>&lt;repo&gt;/assets/game</c>
    /// — holds nothing but shaders. The search stopped there and the scene reported the pub games
    /// missing while they sat in a build output two candidates further down.
    ///
    /// The candidate list is <see cref="Engine_GameData"/>'s, so this resolves by the same
    /// convention as the banks and honours the same <c>REFII_GAME_DATA</c> override, rather than
    /// keeping a second idea of where the game data is.
    /// </remarks>
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
            // THROUGH EndGame, NOT STRAIGHT TO THE CARD. Back does not mean "show the quit
            // dialogue" — it means "I want to leave", and the game decides what that costs: a
            // confirmation when there is money riding on the roll, the cash-out card when the
            // player has played and is up or down, and an immediate settle when neither applies.
            // Raising the quit card unconditionally asked a player who had bet nothing to confirm
            // abandoning bets they did not have.
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

        // THE TUTORIAL IS ITS OWN STATE MACHINE and does not share the betting phases. It scripts
        // its own rolls, deals its own bets and advances only when the player answers a card, so
        // letting the phase machine run alongside it would have two things driving the same board.
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
    }

    private double _processMs;

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

    /// <summary>
    /// True once the game has settled and said so, so a host knows the session is over.
    /// </summary>
    /// <remarks>
    /// THE GAME'S OWN "GameOver", NOT AN OVERLAY'S FLAG. Every exit runs the same chain —
    /// <c>PubGame::GameOver</c> nets chips against debt and turns the remainder into gold, fades
    /// the board out, and the fade's completion calls <c>GameReallyOver</c>, which issues this
    /// command. Reading an overlay's "the player closed me" flag instead sampled the balance
    /// BEFORE the settlement had run and, on the quit-without-betting path, in place of running it
    /// at all. See [[refii-sample-late-not-early]] in spirit: gate on the settled state.
    ///
    /// This is the signal the guest is waiting on. It sits at state 2 until whoever is drawing the
    /// game hands it back state 9 to settle.
    /// </remarks>
    public bool IsFinished => _ready && _commands.GameOverIssued;

    /// <summary>The player's gold, as an ABSOLUTE balance for the guest to settle against.</summary>
    /// <remarks>
    /// Not winnings. The guest seeded this game's bank from the hero's real gold before the
    /// session and settles by crediting the DIFFERENCE, so handing it a delta would pay twice.
    /// </remarks>
    public long FinalGold => _human?.Gold ?? 0;

    /// <summary>The player's pub-game debt, likewise absolute.</summary>
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

    /// <summary>
    /// The most a shape is rasterized above its authored size.
    /// </summary>
    /// <remarks>
    /// The rasterizer is software and its cost is per PIXEL, so this is a quadratic budget: 3x is
    /// nine times the work of 1x. It is set at 3 because that covers a 640-wide stage filling a
    /// 4K window's height, and past that the authored art has no more detail to expose however
    /// large the canvas is.
    /// </remarks>
    public const float MaxRasterScale = 3f;

    private float StageWidth => Mathf.Max(_stageSize.X, 1f);

    private float StageHeight => Mathf.Max(_stageSize.Y, 1f);

    /// <summary>How far the whole stage is magnified to fill the window.</summary>
    private float StageFit()
    {
        Vector2 viewport = GetViewport().GetVisibleRect().Size;
        return Mathf.Min(viewport.X / StageWidth, viewport.Y / StageHeight);
    }

    /// <summary>
    /// The oversampling the scene rasterizes at.
    /// </summary>
    /// <remarks>
    /// THE SETTING DECIDES, AND ITS DEFAULT IS 1. Following the window is the version that makes
    /// the board sharp, and it is not the default because it is expensive in a way nothing else
    /// in this pipeline is: the shapes are rasterized on the CPU at load, so the cost is quadratic
    /// in this number. Keystone measures 234 canvases at 109 MB and 4.6 s at 1x, and 348 MB and
    /// 11.9 s at 2.5x. Seven extra seconds in front of a pub minigame is worse than a soft board,
    /// so a player who wants the sharp one asks for it — see
    /// <see cref="Engine_TexSettings.PubGameRasterScale"/>.
    ///
    /// FOLLOWING THE WINDOW IS ROUNDED UP TO A HALF STEP, and rounded rather than followed
    /// exactly, because changing it throws away every rasterized shape: tracking the fit factor
    /// continuously would rebuild the entire tree on every pixel of a window drag. Up rather than
    /// down so the canvas is never smaller than what is on screen — a shortfall is visible as
    /// blur, an excess is not visible at all.
    ///
    /// Never below 1 either way: the stage is only ever magnified to fit, never shrunk below its
    /// authored size in a way that would make a smaller canvas correct.
    /// </remarks>
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

        // NOTHING TO DO ON THE VECTOR PATH, and that is its whole advantage: the geometry is the
        // same geometry at every size, so a resize is a transform and not a rebuild.
        if (_scene is null || _scene.Vector)
        {
            return;
        }

        // THE RASTER SCALE FOLLOWS THE WINDOW. Set here rather than once at load so a resize —
        // or a borderless-to-fullscreen switch — re-rasterizes instead of leaving the stage
        // showing canvases built for the old size. A no-op unless the quantised value moved.
        _scene.TextureScale = RasterScale();

        if (!Mathf.IsEqualApprox(_scene.TextureScale, _reportedRaster))
        {
            _reportedRaster = _scene.TextureScale;
            GD.Print(
                $"[pubgame] viewport {viewport.X:0}x{viewport.Y:0}, stage fit {scale:0.00}x, " +
                $"rasterizing at {_scene.TextureScale:0.0}x");
        }
    }

    /// <summary>The raster scale the last log line named, so a per-frame Fit does not flood.</summary>
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
