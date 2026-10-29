using System.Collections.Generic;
using System.Globalization;

namespace Refii.Titles.Fable2.PubGames.Framework;

public abstract class PubGame
{
    protected PubGame(IPubGameHost host, IPubTimer timer, IPubRandom random)
    {
        Host = host;
        Timer = timer;
        Random = random;
        Input = new Input();
        Audio = new Audio(host);
        PlayerInfo = new PlayerInfo();
        Localizer = new Localizer();
        CallbackManager = new CallbackManager(timer);
        Pad = null;
        VariantName = string.Empty;
        GameIndex = 0;
        StarPoints = new List<int>();
        ConceptArtPoints = new List<int>();
        GameUnlockPoints = new List<int>();
        GameUnlockName = new List<string>();
        GameUnlockMin = new List<int>();
        GameUnlockMax = new List<int>();
        StarCredit = new List<int>();
        AIPlayerNames = new List<string>();
        WideScreen = false;
        SkinIndex = 0;
        Tournament = false;
        Tutorial = false;
        TournamentRounds = 1;
        TournamentPayouts = new List<int>();
        TournamentStartingChips = 1500;
        TournamentItemAlreadyWon = false;
        UnlockedItemIndex = -1;
        TournamentShootout = false;
        TournamentFinalTable = false;
        MinRank = 0;
        MinGamePoints = 0;
        MinBet = 0;
        MaxBet = 0;
        BuyIn = 0;
        NumberOfAIPlayers = 0;
        TournamentResultReported = false;
        TournamentPlace = -1;
        AllowPoints = false;
        BetsMade = false;
        PlayerStandings = new List<Player>();
        MergableCharacters = new List<Player>();
        MergeSuccess = false;
        RetailGame = false;
        TrialVersion = false;
        TestingMode = false;
        SoundVolume = 5;
        MusicVolume = 5;
        AutoPlay = false;
    }

    protected IPubGameHost Host { get; }

    protected IPubTimer Timer { get; }

    public IPubRandom Random { get; }

    public Input Input { get; }

    public Audio Audio { get; }

    public PlayerInfo PlayerInfo { get; }

    public Localizer Localizer { get; }

    public CallbackManager CallbackManager { get; }

    public IPubOverlayManager? OverlayManager { get; set; }

    public GamePad? Pad { get; protected set; }

    public string VariantName { get; set; }

    public int GameIndex { get; set; }

    public List<int> StarPoints { get; }

    public List<int> ConceptArtPoints { get; }

    public List<int> GameUnlockPoints { get; }

    public List<string> GameUnlockName { get; }

    public List<int> GameUnlockMin { get; }

    public List<int> GameUnlockMax { get; }

    public List<int> StarCredit { get; }

    public List<string> AIPlayerNames { get; }

    public bool WideScreen { get; set; }

    public int SkinIndex { get; set; }

    public bool Tournament { get; set; }

    public bool Tutorial { get; set; }

    public int TournamentRounds { get; set; }

    public List<int> TournamentPayouts { get; }

    public int TournamentStartingChips { get; set; }

    public bool TournamentItemAlreadyWon { get; set; }

    public int UnlockedItemIndex { get; set; }

    public bool TournamentShootout { get; set; }

    public bool TournamentFinalTable { get; set; }

    public int MinRank { get; set; }

    public int MinGamePoints { get; set; }

    public int MinBet { get; set; }

    public int MaxBet { get; set; }

    public int BuyIn { get; set; }

    public int NumberOfAIPlayers { get; set; }

    public bool TournamentResultReported { get; protected set; }

    public int TournamentPlace { get; protected set; }

    public bool AllowPoints { get; set; }

    public bool BetsMade { get; set; }

    public List<Player> PlayerStandings { get; }

    public List<Player> MergableCharacters { get; set; }

    public bool MergeSuccess { get; set; }

    public bool RetailGame { get; set; }

    public bool TrialVersion { get; set; }

    public bool TestingMode { get; set; }

    /// <summary>0..10 in steps of 0.5 — the shim settings sliders move it by halves (0x0082ed).</summary>
    public double SoundVolume { get; set; }

    public double MusicVolume { get; set; }

    public bool AutoPlay { get; set; }

    public bool GameIsExiting { get; private set; }

    public bool Saving { get; private set; }

    protected virtual void PreInitialize()
    {
    }

    protected virtual void PostInitialize()
    {
    }

    protected virtual void GameExiting()
    {
    }

    protected virtual void CleanUp()
    {
    }

    public virtual Player CreatePlayerObject(
        int id, bool isAI, string name, int gold, int debt,
        int starRating, int points, int gamePoints) =>
        new(id, isAI, name, gold, debt, starRating, points, gamePoints);

    public virtual bool EndGameWarning() => false;

    public virtual void BankingMenuDispelled()
    {
    }

    /// <summary>Empty in AS2's base; <c>KillGameHelp</c> calls it (0x009aad).</summary>
    public virtual void GameHelpDispelled()
    {
    }

    public virtual void Initialize()
    {
        CallbackManager.RemoveAllCallbacks();
        RegisterOverlayManager();
        PreInitialize();
    }

    /// <summary>
    /// Makes the overlay manager callback 0 (<c>PubGame::Initialize</c> 0x00161b). Its <c>Run</c>
    /// sets its own <c>Blocking</c>, and an ungrouped blocking callback ends
    /// <see cref="CallbackManager.DoCallbacks"/> — which is how every dialog freezes the game.
    /// </summary>
    public void RegisterOverlayManager()
    {
        if (OverlayManager is Callback overlays)
        {
            CallbackManager.AddCallbackObjectFirst(overlays);
        }
    }

    public virtual void Run()
    {
        for (int i = 0; i <= 82; i++)
        {
            AddAIPlayer(i + 1, Localizer.LocalizeString("txtNPCName" + i, null));
        }

        Player? human;
        if (Tournament)
        {
            for (int i = 0; i < PlayerInfo.NumPlayers; i++)
            {
                Player? player = PlayerInfo.GetPlayer(i);
                if (player is not null)
                {
                    player.Chips = TournamentStartingChips;
                }
            }
        }
        else if (!Tutorial)
        {
            human = PlayerInfo.GetPlayer(0);
            if (human is not null)
            {
                human.Chips += BuyIn;
            }
        }

        if (BuyIn > 0)
        {
            human = PlayerInfo.GetPlayer(0);
            if (human is not null)
            {
                human.Debt += BuyIn;
            }

            UpdatePlayerHoldings();
        }

        PostInitialize();

        if (Tournament)
        {
            for (int i = 0; i < PlayerInfo.NumPlayers; i++)
            {
                Player? player = PlayerInfo.GetPlayer(i);
                if (player is not null && player.IsSeated)
                {
                    PlayerStandings.Add(player);
                }
            }

            if (!TrialVersion && BuyIn > 0)
            {
                AllowPoints = true;
                PointsEarned(BuyIn);
            }

            Host.Command(PubCommand.TournamentBuyIn, BuyIn.ToString());
        }

        AllowPoints = !Tournament && !Tutorial && !TrialVersion;

        // AFTER THE BUY-IN. `PubGame::Run` calls `overlayManager.Init()` here, once the chips have
        // been lent and the matching debt taken, so the bank it snapshots is the one the player is
        // actually sitting down with.
        OverlayManager?.Init();
        CallbackManager.ResetTimer();
        Host.Command(PubCommand.Running, null);
    }

    /// <summary>
    /// <c>PubGame::OnEnterFrame</c> (0x001a06-0x001a6e): <c>input.Update()</c>, pad 0 (else the
    /// keyboard pad), <c>DoCallbacks</c>. <paramref name="ticks"/> is the elapsed milliseconds;
    /// null reads the manager's clock as AS2 does. Does nothing once <see cref="CleanUpGame"/>
    /// has cleared <c>_root.onEnterFrame</c> (0x001dae).
    /// </summary>
    public void OnEnterFrame(double? ticks = null)
    {
        if (_cleanedUp)
        {
            return;
        }

        Input.Update();
        Pad = Input.GetPadState(0) ?? Input.GetKeyboardPadState();
        CallbackManager.DoCallbacks(ticks);
    }

    /// <summary><c>PubGame::UpdatePadAfterPause</c> (0x0019db): one more <c>input.Update()</c>.</summary>
    public void UpdatePadAfterPause() => Input.Update();

    /// <summary><c>PubGame::ActivateShimPause</c> (0x0029f1).</summary>
    public void ActivateShimPause() => OverlayManager?.ActivateShimPauseMenu();

    /// <summary><c>PubGame::ActivatedGameHelp</c> (0x0029b6).</summary>
    public void ActivatedGameHelp() => OverlayManager?.ActivateGameHelp();

    private bool _cleanedUp;

    public virtual void EndGame()
    {
        bool casual = !Tournament && !Tutorial;
        bool warn = (Tournament && BuyIn > 0 && !TournamentResultReported) ||
                    (casual && EndGameWarning());
        if (warn)
        {
            BetsMade = true;
            OverlayManager?.ActivateQuitConfirmation();
            return;
        }

        if (casual && BetsMade)
        {
            OverlayManager?.ActivateCashOut();
            return;
        }

        GameOver();
    }

    public virtual void GameOver()
    {
        Audio.PlaySound("G_quit");
        GameIsExiting = true;
        GameExiting();

        Player? human = PlayerInfo.GetPlayer(0);
        if (human is null)
        {
            return;
        }

        if (Tournament || Tutorial)
        {
            human.Chips = 0;
            if (Tournament && TournamentResultReported)
            {
                human.Chips = GetTournamentPayout(TournamentPlace);
            }
        }

        int debt = human.Debt - human.Chips;
        if (debt < 0)
        {
            human.Gold -= debt;
            debt = 0;
        }

        human.Chips = 0;
        human.Debt = debt;
        UpdatePlayerHoldings();
        if (!Saving)
        {
            OverlayManager?.ActivateFadeOut();
        }
    }

    public void GameReallyOver()
    {
        CleanUpGame();
        Host.Command(
            RetailGame && Tutorial ? PubCommand.LoadGameSettings : PubCommand.GameOver, null);
    }

    /// <summary>
    /// <c>PubGame::CleanUpGame</c> (0x001dae-0x001e02). The game stops being ticked here —
    /// <c>_root.onEnterFrame = null</c> — so the emptied manager is never run again.
    /// </summary>
    private void CleanUpGame()
    {
        _cleanedUp = true;
        CallbackManager.RemoveAllCallbacks();
        OverlayManager?.CleanUp();
        CleanUp();
        OverlayManager = null;
    }

    public void TournamentItemWon(int itemId)
    {
        if (itemId == UnlockedItemIndex)
        {
            TournamentItemAlreadyWon = true;
        }
    }

    public void AddHumanPlayer(
        int id, string name, int gold, int debt, int starRating, int points, int gamePoints)
    {
        PlayerInfo.AddPlayer(
            id, CreatePlayerObject(id, false, name, gold, debt, starRating, points, gamePoints));
    }

    public void AddAIPlayer(int id, string name)
    {
        PlayerInfo.AddPlayer(id, CreatePlayerObject(id, true, name, 0, 0, 1, 0, 0));
    }

    public void AddMergableCharacter(int id, string name, int gold, int debt, int points)
    {
        MergableCharacters.Add(CreatePlayerObject(id, false, name, gold, debt, 0, points, 0));
    }

    public void ChangeStarRating(int id, int rating, bool hideDialog = false)
    {
        Player? player = PlayerInfo.GetPlayer(id);
        if (player is null)
        {
            return;
        }

        player.StarRating = rating;
        if (!hideDialog)
        {
            OverlayManager?.ActivateStarRatingDialog();
        }

        Host.Command(PubCommand.NewRank, "newRank:" + player.StarRating);
    }

    public void TournamentCompleted()
    {
        UpdateTournamentStandings();
        Player? human = PlayerInfo.GetPlayer(0);
        if (human is null)
        {
            return;
        }

        TournamentPlace = human.Rank;
        if (TournamentShootout && !TournamentFinalTable)
        {
            TournamentPlace = (TournamentPlace * 5) - Random.Range(5);
        }

        TournamentResultReported = true;
        int payout = GetTournamentPayout(TournamentPlace);
        int finalChips = TournamentPlace != 1 ? 0 : human.Chips;
        Host.Command(
            PubCommand.TournamentCompleted,
            $"minRank:{MinRank}; place:{TournamentPlace}; payout:{payout}; " +
            $"totalWinnings:{payout - BuyIn}; finalChips:{finalChips}");
    }

    public void AchievementEarned(string achievementName)
    {
        Host.Command(PubCommand.AchievementEarned, "achievementName:" + achievementName);
    }

    public virtual void PointsEarned(int pointsEarned)
    {
        if (pointsEarned > 0)
        {
            BetsMade = true;
        }

        if (!AllowPoints)
        {
            return;
        }

        Player? human = PlayerInfo.GetPlayer(0);
        if (human is null)
        {
            return;
        }

        int beforePoints = human.Points;
        int beforeGamePoints = human.GamePoints;
        human.Points += pointsEarned;
        human.GamePoints += pointsEarned;
        human.SessionPointsEarned += pointsEarned;
        Host.Command(PubCommand.PointsEarned, "points:" + pointsEarned);

        if (human.StarRating < 5 &&
            beforePoints < StarPointsAt(human.StarRating) &&
            human.Points >= StarPointsAt(human.StarRating))
        {
            int rating = human.StarRating + 1;
            while (rating < 5 && human.Points >= StarPointsAt(rating))
            {
                rating++;
            }

            ChangeStarRating(0, rating);
        }
        else if (!RetailGame)
        {
            bool concept = false;
            for (int i = 0; i < ConceptArtPoints.Count; i++)
            {
                if (beforePoints < ConceptArtPoints[i] && human.Points >= ConceptArtPoints[i])
                {
                    concept = true;
                }
            }

            bool table = false;
            int unlock = 0;
            for (; unlock < GameUnlockPoints.Count; unlock++)
            {
                if (beforeGamePoints < GameUnlockPoints[unlock] &&
                    human.GamePoints >= GameUnlockPoints[unlock])
                {
                    table = true;
                    break;
                }
            }

            if (table)
            {
                OverlayManager?.ActivateStarRatingDialog(
                    table, concept,
                    unlock < GameUnlockName.Count ? GameUnlockName[unlock] : null,
                    unlock < GameUnlockMin.Count ? GameUnlockMin[unlock] : 0,
                    unlock < GameUnlockMax.Count ? GameUnlockMax[unlock] : 0);
            }
            else if (concept)
            {
                OverlayManager?.ActivateStarRatingDialog(table, concept);
            }
        }

        if (StarPoints.Count > 1 &&
            (beforePoints < StarPoints[1] && human.Points >= StarPoints[1] ||
             GameUnlockPoints.Count > 0 && beforeGamePoints < GameUnlockPoints[0] &&
             human.GamePoints >= GameUnlockPoints[0]))
        {
            AchievementEarned("G_UnlockGame");
        }
    }

    private int StarPointsAt(int index) =>
        index >= 0 && index < StarPoints.Count ? StarPoints[index] : int.MaxValue;

    public void UpdatePlayerHoldings()
    {
        Player? human = PlayerInfo.GetPlayer(0);
        if (human is not null)
        {
            Host.Command(
                PubCommand.UpdatePlayerHoldings, $"gold:{human.Gold}; debt:{human.Debt}");
        }
    }

    public void SetLocalizedString(string id, string locText) => Localizer.SetString(id, locText);

    public void SetLocalizedAttribute(string id, string attribute, string? setting) =>
        Localizer.SetAttribute(id, attribute, setting);

    public void SetPadValue(
        int index, double leftTrigger, double rightTrigger,
        double thumbLX, double thumbLY, double thumbRX, double thumbRY, int buttons) =>
        Input.SetPadValue(
            index, leftTrigger, rightTrigger, thumbLX, thumbLY, thumbRX, thumbRY, buttons);

    public void StartSave()
    {
        Saving = true;
        OverlayManager?.ShowSaveIcon(true);
    }

    public void EndSave()
    {
        Saving = false;
        OverlayManager?.ShowSaveIcon(false);
        if (GameIsExiting)
        {
            OverlayManager?.ActivateFadeOut();
        }
    }

    public void UpdateVolumeSettings()
    {
        Host.Command(
            PubCommand.UpdateSoundVolume,
            "volume:" + SoundVolume.ToString(CultureInfo.InvariantCulture));
        Host.Command(
            PubCommand.UpdateMusicVolume,
            "volume:" + MusicVolume.ToString(CultureInfo.InvariantCulture));
    }

    public void UpdateTournamentStandings()
    {
        PlayerStandings.Sort(TournamentShootout ? SortPlayersByChipsAndTable : SortPlayersByChips);
        for (int i = 0; i < PlayerStandings.Count; i++)
        {
            PlayerStandings[i].Rank = i + 1;
        }
    }

    public static int SortPlayersByChipsAndTable(Player? a, Player? b)
    {
        if (ReferenceEquals(a, b))
        {
            return 0;
        }

        if (a is null)
        {
            return 1;
        }

        if (b is null)
        {
            return -1;
        }

        if (a.Table != b.Table)
        {
            return a.Table < b.Table ? -1 : 1;
        }

        return CompareChips(a, b);
    }

    public static int SortPlayersByChips(Player? a, Player? b)
    {
        if (ReferenceEquals(a, b))
        {
            return 0;
        }

        if (a is null)
        {
            return 1;
        }

        if (b is null)
        {
            return -1;
        }

        return CompareChips(a, b);
    }

    private static int CompareChips(Player a, Player b)
    {
        if (a.Busted && b.Busted)
        {
            if (a.BustedRank != b.BustedRank)
            {
                return a.BustedRank < b.BustedRank ? -1 : 1;
            }
        }
        else
        {
            if (a.Busted)
            {
                return 1;
            }

            if (b.Busted)
            {
                return -1;
            }

            if (a.Chips != b.Chips)
            {
                return a.Chips < b.Chips ? 1 : -1;
            }
        }

        if (a.ID != b.ID)
        {
            return a.ID < b.ID ? -1 : 1;
        }

        return 0;
    }

    public int GetTournamentPayout(int place)
    {
        int index = place - 1;
        return index >= 0 && index < TournamentPayouts.Count ? TournamentPayouts[index] : 0;
    }
}
