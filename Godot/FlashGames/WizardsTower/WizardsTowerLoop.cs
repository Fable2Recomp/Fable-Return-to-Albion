using System;
using System.Collections.Generic;

using Refii.FlashGames.Framework;

namespace Refii.FlashGames.WizardsTower;

public sealed partial class WizardsTower
{
    protected override void PreInitialize()
    {
        StackedDeck = new List<int>();
        InitTutorialData();

        IPubClip? safeArea = Clip("SafeArea");
        if (safeArea is not null)
        {
            safeArea.Visible = false;
        }

        for (var i = 0; i < 5; i++)
        {
            ResetSeatChrome(i);
        }

        IPubClip? message = Clip("textmessage");
        if (message is not null)
        {
            message.Alpha = 0;
            SetIfPresent(message, "body", string.Empty);
            SetIfPresent(message, "body2", string.Empty);
            SetIfPresent(message, "gameName", string.Empty);
            SetIfPresent(message, "gameLimits", string.Empty);
            message.SwapDepths(3500);
        }

        Hide("BetInfo");
        Hide("OfferInfo");
        HideClip(Clip("OfferInfo")?.GetChild("BustedX"));
        Hide("BustedRowX");
        Hide("CheatsClip");

        foreach (string name in new[] { "Launch", "Land" })
        {
            IPubClip? clip = Clip(name);
            clip?.GotoAndStop(clip.TotalFrames);
        }

        foreach (string name in new[]
        {
            "AButtonText", "BButtonText", "YButtonText", "RTButtonText", "LTButtonText",
            "LSButtonText", "Abutton", "Bbutton", "Ybutton", "LSbutton", "RTbutton", "LTbutton",
        })
        {
            Hide(name);
        }

        for (var i = 0; i < 5; i++)
        {
            IPubClip? status = Clip("Status" + i);
            if (status is null)
            {
                continue;
            }

            SetIfPresent(status, "StatusText", string.Empty);
            HideClip(status.GetChild("BustedX"));
        }

        IPubClip? hands = Root?.AttachMovie("HandsClip", "HandsClip", 997);
        if (hands is not null)
        {
            hands.X = 116;
            hands.Y = 741;
            SetIfPresent(hands, "HandsLabel", string.Empty);
        }

        IPubClip? npcStatus = Clip("Status1");
        if (npcStatus is not null)
        {
            NPCStatusXScale = npcStatus.XScale;
            NPCStatusYScale = npcStatus.YScale;
        }
    }

    protected override void PostInitialize()
    {
        if (!Tournament && !Tutorial && NoSinglePlayerAIs)
        {
            foreach (string name in new[]
            {
                "AButtonText", "BButtonText", "YButtonText", "RTButtonText", "LTButtonText",
                "LSButtonText", "Abutton", "Bbutton", "Ybutton", "LSbutton", "RTbutton", "LTbutton",
            })
            {
                IPubClip? clip = Clip(name);
                if (clip is not null)
                {
                    clip.X += 96;
                }
            }
        }

        SetIfPresent(Clip("LSButtonText"), "StatusText", LocString("txtChangeBet"));

        if (Tournament)
        {
            NoSinglePlayerAIs = false;
            IPubClip? candle = Clip("Candle");
            if (candle is not null)
            {
                candle.SwapDepths(3413);
                candle.RemoveMovieClip();
            }
        }

        Clip("overlays")?.SwapDepths(5000);

        if (BuyIn > 0)
        {
            EffectiveBuyIn = BuyIn;
        }

        for (var i = 0; i < 5; i++)
        {
            int chips = Tournament ? TournamentStartingChips
                : i == 0 && !Tutorial ? 0
                : EffectiveBuyIn;
            LocText(
                Clip("ChipTotal" + i)?.GetChild("TextField"), "txtChipTotal", Vars("CHIPS", chips));
        }

        Action.DefaultCallbackManager = new CallbackManager(Timer);
        CallbackManager.AddCallbackFunction(GameLoop, this);

        NumberOfTables = (int)Math.Floor(NumberOfAIPlayers / 5.0) + 1;
        ResetGameRules();
        PresetAIList = new List<int>();
    }

    public WizardsTowerOverlayManager? Overlays { get; private set; }

    public WizardsTowerOverlayManager CreateOverlayManager()
    {
        Overlays = new WizardsTowerOverlayManager(this, Root!);
        OverlayManager = Overlays;
        return Overlays;
    }

    public void ResetGameRules()
    {
        Action.DefaultCallbackManager?.RemoveAllCallbacks();
        DisposeGameView();
        NoPlayerBets = false;

        for (var i = 0; i < 5; i++)
        {
            ResetSeatChrome(i);
        }

        Games = new List<GameLogic>();
        YButtonText = Tournament ? string.Empty : "txtBanking";

        if (Tournament)
        {
            TournamentShootout = true;
        }

        if (Overlays is not null)
        {
            Overlays.AllowQuickTips = !Tutorial;
            Overlays.BlockingOverride = true;
        }

        TournamentOver = false;
        NoMoreHands = false;
        TournamentHandsPlayed = 0;

        for (var i = 0; i < NumberOfTables; i++)
        {
            Games.Add(new GameLogic(
                this, i, MinBet, RangeOfCards, NumberOfCards, NumberOfHeroes, NumberOfRows,
                AIPlayTable, i > 0, Random));
        }

        if (Tournament)
        {
            UpdateTournamentStandings();
        }

        VGame = Games[0];
        if (Tutorial)
        {
            VGame.Players[0].Stakes = 0;
        }

        InitGameView();

        for (var i = 1; i < 5; i++)
        {
            IPubClip? status = Clip("Status" + i);
            if (status is not null)
            {
                status.XScale = (float)NPCStatusXScale;
                status.YScale = (float)NPCStatusYScale;
            }
        }

        SpeedFactor = 1;
        HurryUpFactor = 1;
        Action.SetGameSpeed(SpeedFactor);
        GlintTime = 0;
        IdleTime = 0;
        VGame.AutoPlay = AutoPlay;
    }

    protected override void CleanUp()
    {
        Action.DefaultCallbackManager?.RemoveAllCallbacks();
        Action.Deallocate();
        DisposeGameView();
        Games = new List<GameLogic>();
        Cards.Clear();
    }

    public void InitGameView()
    {
        VDeck = VGame!.Deck;
        VPlayers = VGame.Players;
        VGame.ReadyToContinue = false;

        if (!Tournament || !TournamentFinalTable)
        {
            Root?.CreateEmptyMovieClip("Markers", 996);
            Root?.CreateEmptyMovieClip("TotalBoxes", 998);
            Cards.CardClips = Root?.CreateEmptyMovieClip("CardClips", 1000);

            BoardXPosition = new List<double>();
            BoardYPosition = new List<double>();
            ShowXPosition = new List<double>();
            ShowYPosition = new List<double>();
            ShowAngle = new List<double>();

            _xPos = XCenter;
            _yPos = YTowerTop;
            DeckXPosition = DefaultDeckXPosition;
            DeckYPosition = DefaultDeckYPosition;
            _xShowPos = XCenter;
            _yShowPos = YSpreadTop;
            _cardwidth = 105;
            _cardheight = 210;
            _rowPos = 1;
            _rowSize = 0;
            _scale = 1100.0 / (VDeck.NumberOfRows + 3);
            _cardwidth *= _scale / 100;
            _cardheight *= _scale / 100;
            _t = 2;
            _m = 2;

            for (var i = 0; i < VDeck.TotalCards; i++)
            {
                if (i % 10 == 0 || i == VDeck.TotalCards - 1)
                {
                    Action.BlockOn(Action.RunOnceWithData(this, CreateCardMovie, i));
                }
                else
                {
                    Action.RunOnceWithData(this, CreateCardMovie, i);
                }
            }

            IPubClip? textAnimation = Root?.AttachMovie("GoldJackpot", "TextAnimation", 4995);
            if (textAnimation is not null)
            {
                textAnimation.X = 900;
                textAnimation.Y = 600;
            }
        }

        IPubClip? anim = Clip("TextAnimation");
        if (anim is not null)
        {
            anim.GotoAndStop(1);
            anim.Alpha = 0;
        }

        if (!Tournament || !TournamentFinalTable)
        {
            DeckXPosition -= _cardwidth / 4;
            DeckYPosition += _cardheight / 4;

            Clip("foregroundclip")?.SwapDepths(4000);
            Clip("Status0")?.SwapDepths(3006);
            Clip("ChipChange4")?.SwapDepths(3004);
            Clip("Status4")?.SwapDepths(3002);
            Clip("ChipTotal4")?.SwapDepths(2998);
            Clip("Stack4A")?.SwapDepths(2992);
            Clip("Stack4B")?.SwapDepths(2994);
            Clip("Stack4C")?.SwapDepths(2996);
            Clip("Name0")?.SwapDepths(999);
            Clip("BlackMask")?.SwapDepths(10999);

            LocText(Clip("BetInfo")?.GetChild("BetLabel"), "txtBetLabel");
            LocText(Clip("BetInfo")?.GetChild("WinLabel"), "txtWinLabel");
        }

        LocText(
            Clip("BetInfo")?.GetChild("BetAmount"), "txtInfoAmount",
            Vars("CHIPS", VGame.Players[0].Stakes));
        LocText(Clip("BetInfo")?.GetChild("WinAmount"), "txtInfoAmount", Vars("CHIPS", 0));

        Hide("OfferInfo");
        Hide("OfferAnimating");

        for (var i = 0; i < 5; i++)
        {
            IPubClip? status = Clip("Status" + i);
            if (status is null)
            {
                continue;
            }

            SetIfPresent(status, "StatusText", string.Empty);
            HideClip(status.GetChild("BustedX"));
        }

        SetIfPresent(Clip("HandsClip"), "HandsLabel", string.Empty);
        HideClip(Clip("OfferInfo")?.GetChild("BustedX"));
        Hide("BustedRowX");

        if (Tournament && TournamentFinalTable)
        {
            for (var row = 2; row <= VDeck.NumberOfRows; row++)
            {
                HideClip(Clip("TotalBoxes")?.GetChild("Total" + row));
            }
        }

        if (Tournament)
        {
            YButtonText = string.Empty;
            SetIfPresent(Clip("YButtonText"), "StatusText", string.Empty);
        }

        SetIfPresent(Clip("AButtonText"), "StatusText", string.Empty);
        SetIfPresent(Clip("BButtonText"), "StatusText", string.Empty);

        if (Tournament && TournamentFinalTable)
        {
            FinishGameViewInit();
        }
    }

    public void DisposeGameView()
    {
        if (!Tournament || !TournamentFinalTable)
        {
            BoardXPosition = new List<double>();
            BoardYPosition = new List<double>();
            ShowXPosition = new List<double>();
            ShowYPosition = new List<double>();
            ShowAngle = new List<double>();

            if (VGame is not null && VDeck is not null)
            {
                for (var i = 0; i < VDeck.TotalCards; i++)
                {
                    Card(i)?.RemoveMovieClip();
                }

                Cards.CardClips?.RemoveMovieClip();
                Cards.CardClips = null;
                Cards.Clear();

                for (var row = 2; row <= VDeck.NumberOfRows; row++)
                {
                    Clip("TotalBoxes")?.GetChild("Total" + row)?.RemoveMovieClip();
                }

                Clip("TotalBoxes")?.RemoveMovieClip();

                for (var row = 2; row <= VDeck.NumberOfRows; row++)
                {
                    Clip("Markers")?.GetChild("Markers" + row)?.RemoveMovieClip();
                }

                Clip("Markers")?.RemoveMovieClip();
                Clip("TextAnimation")?.RemoveMovieClip();
            }
        }

        VGame = null;
        VDeck = null;
        VPlayers = new List<TowerPlayer>();
    }

    public bool GameLoop(double ticks, object? callbackData, bool blocked)
    {
        IPubClip? mask = Clip("BlackMask");
        if (mask is not null && mask.Alpha > 0)
        {
            Action.DefaultCallbackManager?.DoCallbacks(ticks);
            if (mask.Alpha == 0 && Overlays is not null)
            {
                Overlays.BlockingOverride = false;
            }

            return false;
        }

        if (Overlays is not null
            && (Overlays.AbsorbingInput || Overlays.QuickTipsShowing
                || Overlays.StandingsShowing))
        {
            return false;
        }

        if (VGame is null || VDeck is null || Pad is null)
        {
            return false;
        }

        var blockedByTutorial = false;
        bool anyInput = Pad.LeftShoulderPressed || Pad.RightShoulderPressed || Pad.BtnAPressed
            || Pad.BtnBPressed || Pad.BtnXPressed || Pad.BtnYPressed || Pad.StartPressed
            || Pad.BackPressed || CashoutAlertDialog || Pad.LeftThumbPressed
            || Pad.RightThumbPressed || StickDelay > 0 || PadDelay > 0;

        if (Pad.LeftShoulderPressed || Pad.RightShoulderPressed || Pad.BtnAPressed
            || Pad.BtnBPressed || Pad.BtnXPressed || Pad.BtnYPressed || Pad.StartPressed
            || Pad.BackPressed || Pad.LeftThumbPressed || Pad.RightThumbPressed)
        {
            LimitReached = false;
        }

        if (anyInput || VGame.CurrentRow != 1)
        {
            GlintTime = 0;
        }
        else if (!Tutorial)
        {
            GlintTime += ticks;
            if (GlintTime > 30000)
            {
                GlintTime = -3000;
            }
        }

        if (anyInput || VGame.CurrentRow == 1)
        {
            IdleTime = 0;
            if (anyInput)
            {
                ResetIdleFlags();
            }
        }
        else
        {
            IdleTime += ticks;
            if (IdleTime > 60000)
            {
                IdleTime = 0;
            }
        }

        if (Tournament)
        {
            var canPlay = false;
            if (Games[0].HandsPlayed < TournamentRounds)
            {
                if (!NoMoreHands)
                {
                    if (VPlayers[0].Stakes >= MinBet)
                    {
                        canPlay = true;
                    }
                }
                else
                {
                    Games[0].HandsPlayed++;
                }
            }

            if (!NoMoreHands && !canPlay)
            {
                NoMoreHands = true;
                SetButtonText("A", string.Empty);
            }

            if (TournamentOver && !TournamentResultReported && VGame.ReadyToContinue)
            {
                TournamentCompleted();
            }
        }

        if (TutorialPauseUntilA)
        {
            if (!TutorialInputBlock && Pad.BtnAPressed)
            {
                TutorialPauseUntilA = false;
                if (!VGame.ReadyToContinue || TutorialIgnoreButtonPress)
                {
                    blockedByTutorial = true;
                }
            }
            else
            {
                blockedByTutorial = true;
            }
        }

        if (TutorialPauseUntilB)
        {
            if (TutorialStep == TutorialLastStep && Overlays is not null)
            {
                Overlays.AllowQuickTips = true;
            }

            if (!TutorialInputBlock && Pad.BtnBPressed)
            {
                if (TutorialStep == TutorialLastStep)
                {
                    EndGame();
                    return false;
                }

                TutorialPauseUntilB = false;
                if (!VGame.ReadyToContinue)
                {
                    blockedByTutorial = true;
                }
            }
            else
            {
                blockedByTutorial = true;
            }
        }

        if (TutorialPauseUntilLS)
        {
            if (VPlayers[0].Stakes == 15)
            {
                InsertTutorialCallback();
                InsertTutorialCallback();
                InsertTutorialCallback();
                TutorialPauseUntilLS = false;
            }

            blockedByTutorial = true;
        }

        if (StickDelay > -500)
        {
            StickDelay -= ticks;
        }
        else if (StickNeutral())
        {
            LimitReached = false;
        }

        HandleBetInput(blockedByTutorial);

        if (!NextHand && VGame.CurrentRow != 1
            && (!VGame.ReadyToContinue || VPlayers[0].Out)
            && (Pad.BtnAPressed || Pad.BtnBPressed)
            && (!blockedByTutorial || TutorialPauseUntilA || TutorialPauseUntilB
                || TutorialPauseUntilLS))
        {
            if (VPlayers[0].Out)
            {
                NextHand = true;
                SetButtonText(
                    "A", VDeck.RowIsBusted(VGame.CurrentRow) ? "txtPlayAgain" : string.Empty);
            }

            if (!FrameStep)
            {
                HurryUpFactor = 5;
                Action.SetGameSpeed(SpeedFactor * HurryUpFactor * PlayerOutFactor);
            }
        }

        if (ShowingDeck && !Tutorial && Pad.BtnAPressed && !blockedByTutorial)
        {
            HurryUpFactor = 5;
            Action.SetGameSpeed(SpeedFactor * HurryUpFactor * PlayerOutFactor);
            DelayedPlayHand = true;
        }

        if (VGame.ReadyToContinue)
        {
            RunHandDecision(blockedByTutorial);
        }

        Action.DefaultCallbackManager?.DoCallbacks(ticks);
        return false;
    }

    private bool StickNeutral() =>
        Pad!.ThumbLY < 0.7 && Pad.ThumbLX < 0.7 && Pad.ThumbRY < 0.7 && Pad.ThumbRX < 0.7
        && Pad.ThumbLY > -0.7 && Pad.ThumbLX > -0.7 && Pad.ThumbRY > -0.7 && Pad.ThumbRX > -0.7
        && !Pad.DPadUp && !Pad.DPadDown && !Pad.DPadLeft;

    private void HandleBetInput(bool blockedByTutorial)
    {
        if (LTOverlayDialog
            || !((!blockedByTutorial && !Tutorial)
                || (TutorialPauseUntilLS && VPlayers[0].Stakes != 15))
            || NoMoreHands || StickDelay > 0 || VGame!.CurrentRow != 1 || NoPlayerBets)
        {
            return;
        }

        bool up = Pad!.ThumbLY > 0.8 || Pad.ThumbLX > 0.8 || Pad.ThumbRY > 0.8 || Pad.ThumbRX > 0.8
            || Pad.DPadUp || Pad.DPadRight;
        bool down = Pad.ThumbLY < -0.8 || Pad.ThumbLX < -0.8 || Pad.ThumbRY < -0.8
            || Pad.ThumbRX < -0.8 || Pad.DPadDown || Pad.DPadLeft;

        if (up)
        {
            bool room = VPlayers[0].Stakes < MaxBet
                && (!Tournament || VPlayers[0].Stakes + MinBet <= VPlayers[0].Chips)
                && (!TutorialPauseUntilLS || VPlayers[0].Stakes == 0);
            NudgeBet(room, +MinBet);
        }

        if (down)
        {
            NudgeBet(VPlayers[0].Stakes > MinBet, -MinBet);
        }

        if (Pad.LeftShoulderPressed && !blockedByTutorial && !Tutorial && !NoMoreHands
            && VGame.CurrentRow == 1 && !NoPlayerBets)
        {
            int cap = Math.Max(Math.Min(MaxBet, VPlayers[0].Chips), MinBet);
            if (VPlayers[0].Stakes <= cap && VPlayers[0].Stakes + MinBet > cap)
            {
                return;
            }

            LimitReached = false;
            while (VPlayers[0].Stakes > cap)
            {
                VPlayers[0].Stakes -= MinBet;
            }

            while (VPlayers[0].Stakes + MinBet <= cap)
            {
                VPlayers[0].Stakes += MinBet;
            }

            SetBetAmount(VGame, VPlayers[0].Stakes);
            PlaySound.Once("G_playerBetDing");
            UpdateBetAmountColor();
        }
    }

    private void NudgeBet(bool allowed, int delta)
    {
        if (allowed)
        {
            LimitReached = false;
            VPlayers[0].Stakes += delta;
            SetBetAmount(VGame, VPlayers[0].Stakes);
            PressButtonAnimation("LS");
            StickDelay = 250;
            PlaySound.Once("G_playerBetDing");
            UpdateBetAmountColor();
        }
        else if (!LimitReached)
        {
            PressButtonAnimation("LS");
            StickDelay = 250;
            LimitReached = true;
            PlaySound.Once("G_limitReached?x=200");
        }
    }

    private void RunHandDecision(bool blockedByTutorial)
    {
        if (Pad!.BtnAPressed && !blockedByTutorial)
        {
            Wait.Seconds(0);
            PressButtonAnimation("A");
        }

        if (Pad.BtnBPressed && !blockedByTutorial)
        {
            Wait.Seconds(0);
            PressButtonAnimation("B");
        }

        bool busted = VDeck!.RowIsBusted(VGame!.CurrentRow);

        if (Tutorial && VGame.HandsPlayed > 0)
        {
            VGame.AutoPlay = !blockedByTutorial;
            if (!TutorialDialogActive)
            {
                VGame.AutoPlay = true;
            }
        }

        if (VGame.CurrentRow > 1 && VPlayers[0].Stakes != VPlayers[0].SafetyStakes)
        {
            VPlayers[0].Stakes = VPlayers[0].SafetyStakes;
            SetBetAmount(VGame, VPlayers[0].Stakes);
        }

        bool auto = VPlayers[0].Out || VGame.AutoPlay;

        if (VGame.CurrentRow == 1)
        {
            auto = VGame.AutoPlay;
            if ((Pad.BtnAPressed && !blockedByTutorial && !auto)
                || (!VGame.AnyUndecidedAIs() && auto))
            {
                SafetyFlip = 0;
                if (NoMoreHands)
                {
                    if (!TournamentOver)
                    {
                        UpdateNonvisibleGames();
                    }
                }
                else if (!ShowingDeck)
                {
                    DelayedPlayHand = false;
                    if (VPlayers[0].Stakes > VPlayers[0].Chips)
                    {
                        if (!Tournament)
                        {
                            PlaySound.Once("G_notEnoughMoney");
                            OverlayManager?.ActivateNeedChipsAlert();
                        }
                    }
                    else
                    {
                        FadeOut(Clip("LSbutton"), 0.5);
                        FadeOut(Clip("LSButtonText"), 0.5);
                        ShowLSButton = false;
                        PlaySound.Once("W_dealRow");
                        VGame.PlayHand();
                        BetsMade = true;
                        UpdateNonvisibleGames();
                    }
                }
                else if (!Tutorial)
                {
                    HurryUpFactor = 5;
                    Action.SetGameSpeed(SpeedFactor * HurryUpFactor * PlayerOutFactor);
                    DelayedPlayHand = true;
                }
            }
            else if (NoMoreHands && !TournamentOver)
            {
                UpdateNonvisibleGames();
            }
            else if (!ShowingDeck && SafetyFlip < 8)
            {
                for (var i = 0; i < 5; i++)
                {
                    SetCardFrame((SafetyFlip * 5) + i, -1, null);
                }

                SafetyFlip++;
            }
        }
        else if (busted
            || (VGame.AllPlayersHaveCashedOut() && VGame.CurrentRow == VDeck.NumberOfRows)
            || TutorialHandOverHack)
        {
            PlayerOutFactor = 1;
            NextHand = false;
            if (((Pad.BtnAPressed || Pad.BtnBPressed) && (!blockedByTutorial || TutorialHandOverHack))
                || VGame.AutoPlay)
            {
                Wait.UntilFinished("OfferGroup");
                RestoreOfferScale();
                VGame.HandOver();

                if (MaxBet > 15)
                {
                    if (!Tutorial)
                    {
                        FadeIn(Clip("LSbutton"), 0.5);
                        FadeIn(Clip("LSButtonText"), 0.5);
                    }

                    ShowLSButton = true;
                }

                UpdateNonvisibleGames();
                UpdateBetAmountColor();
                HandsSinceLastCashoutAlert++;
                TutorialHandOverHack = false;
            }
        }
        else if (((Pad.BtnAPressed && !blockedByTutorial && !auto)
                || (!VGame.AnyUndecidedAIs() && auto))
            && VGame.CurrentRow < VDeck.NumberOfRows)
        {
            PlaySound.Once("W_dealRow");
            RestoreOfferScale();
            VGame.PushOn();
            UpdateNonvisibleGames();
        }
        else if ((Pad.BtnBPressed && !blockedByTutorial && !auto)
            || (!VGame.AnyUndecidedAIs() && auto)
            || VGame.CurrentRow == VDeck.NumberOfRows)
        {
            PlaySound.Once("W_takeWinPlayer");
            RestoreOfferScale();

            if (VGame.CurrentRow != VDeck.NumberOfRows)
            {
                // AS2 0x005221-0x005251 passes (0.5, Action.LINEAR, "OfferGroup") to the
                // three-parameter Fade.Out: the group is LINEAR (0), not "OfferGroup".
                FadeOut(Clip("OfferInfo"), 0.5, Action.StyleLinear);
            }

            if (!VDeck.RowIsBusted(VGame.CurrentRow) && !VPlayers[0].Out)
            {
                PlayTakeOfferFlight();
            }

            VGame.CashOut();

            if (Tutorial && VGame.CurrentRow != VDeck.NumberOfRows)
            {
                InsertTutorialCallback();
                TutorialHandOverHack = true;
            }

            UpdateNonvisibleGames();
        }

        if (!NextHand && !VGame.AutoPlay && !FrameStep && !DelayedPlayHand)
        {
            HurryUpFactor = 1;
            Action.SetGameSpeed(SpeedFactor * PlayerOutFactor);
        }
    }

    private void PlayTakeOfferFlight()
    {
        IPubClip? animating = Clip("OfferAnimating");
        IPubClip? launch = Clip("Launch");
        IPubClip? land = Clip("Land");
        if (animating is null)
        {
            return;
        }

        const string group = "OfferAnimatingGroup";

        if (launch is not null)
        {
            Animate.LoopOnce(launch, 0.25, group);
        }

        IPubClip? offerText = animating.GetChild("OfferText");
        if (offerText is not null)
        {
            SetText.Replace(
                offerText,
                (VGame!.CashOutValue * ((double)VPlayers[0].SafetyStakes / 15)).ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                group);
        }

        Fade.In(animating, 0, group);
        MoveTo.Timed(animating, animating.X - 240, animating.Y + 178, 1.4, Action.StyleLinear, group);
        Action.BlockOn(Scale.Up(animating, 2, 0.7, Action.StyleEaseIn, group));
        Scale.Down(animating, 2, 0.7, Action.StyleEaseOut, group);

        if (land is not null)
        {
            Action.Delay(Animate.LoopOnce(land, 0.25, group), 0.5);
        }

        Action.BlockOn(Action.Delay(Fade.Out(animating, 0.4, group), 0.3));
        MoveTo.Instant(animating, animating.X, animating.Y, group);
        Wait.Seconds(1.4);
    }

    private void RestoreOfferScale()
    {
        if (OfferInfoScale == 0)
        {
            return;
        }

        Action.DefaultCallbackManager?.RemoveAllCallbacks("OfferGroup");
        IPubClip? text = Clip("OfferInfo")?.GetChild("TextClip");
        if (text is not null)
        {
            text.XScale = (float)OfferInfoScale;
        }
    }

    public void UpdateNonvisibleGames()
    {
        var again = false;
        var allDone = true;

        for (var i = 1; i < NumberOfTables; i++)
        {
            if (Tournament && Games[i].HandsPlayed >= TournamentRounds)
            {
                continue;
            }

            allDone = false;
            if (!Games[i].ReadyToContinue)
            {
                continue;
            }

            if (Games[i].HandsPlayed > Games[0].HandsPlayed)
            {
                again = false;
                continue;
            }

            bool busted = Games[i].Deck.RowIsBusted(Games[i].CurrentRow);
            if (Games[i].CurrentRow == 1)
            {
                if (!ShowingDeck)
                {
                    Games[i].PlayHand();
                }
            }
            else if (busted || Games[i].AllPlayersHaveCashedOut())
            {
                Games[i].HandOver();
            }
            else if (Games[i].CurrentRow < Games[i].Deck.NumberOfRows)
            {
                Games[i].PushOn();
            }
            else
            {
                Games[i].CashOut();
            }

            if (!again && Games[i].HandsPlayed <= Games[0].HandsPlayed)
            {
                again = true;
                i--;
            }
            else
            {
                again = false;
            }
        }

        if (Tournament)
        {
            int hands = Games[0].HandsPlayed + 1 > TournamentRounds
                ? TournamentRounds
                : Games[0].HandsPlayed + 1;
            SetIfPresent(
                Clip("HandsClip"), "HandsLabel",
                LocString("txtTournamentPlaysLabel", Vars("HANDS", hands, "MAX", TournamentRounds)));
        }

        if (Tournament
            && ((allDone && Games[0].HandsPlayed >= TournamentRounds)
                || (VGame!.CurrentRow == 1 && !VPlayers[0].In && VPlayers[0].Chips < MinBet)))
        {
            UpdateTournamentStandings();
            UpdateStandings();

            if (TournamentFinalTable || PlayerRank != 1)
            {
                TournamentOver = true;
            }
            else if (!FinalTableAnims)
            {
                FinalTableAnims = true;
                SweepCards();
                DoneShowingDeck();
                Wait.UntilFinished();
                Action.RunOnce(this, GotoFinalTable);
            }
        }
    }

    public void GotoFinalTable()
    {
        TournamentFinalTable = true;
        TournamentStartingChips *= 2;

        for (var i = 1; i < NumberOfTables * 5; i++)
        {
            if (i >= PlayerStandings.Count)
            {
                break;
            }

            if (i % 5 == 0)
            {
                while (PresetAIList.Count <= i / 5)
                {
                    PresetAIList.Add(0);
                }

                PresetAIList[i / 5] = PlayerStandings[i].ID;
            }
            else
            {
                Player? seated = PlayerInfo.GetPlayer(PlayerStandings[i].ID);
                if (seated is not null)
                {
                    seated.Table = i % 5;
                }
            }
        }

        NumberOfTables = 1;
        ResetGameRules();
    }

    public void UpdateStandings()
    {
        if (!Tournament)
        {
            return;
        }

        TournamentHandsPlayed = Games[0].HandsPlayed;

        if (Games[0].Players[0].Stakes < MinBet)
        {
            for (var i = 1; i < NumberOfTables; i++)
            {
                if (Games[i].HandsPlayed < TournamentHandsPlayed)
                {
                    TournamentHandsPlayed = Games[i].HandsPlayed;
                }
            }
        }

        Player? human = PlayerInfo.GetPlayer(0);
        if (human is null)
        {
            return;
        }

        PlayerRank = human.Rank;
        if (!TournamentFinalTable)
        {
            human.Rank = NumberOfTables * 5;
        }
        else if (!TournamentOver)
        {
            human.Rank = 5;
        }
    }

    private void ResetSeatChrome(int seat)
    {
        foreach (string suffix in new[] { "A", "B", "C" })
        {
            IPubClip? stack = Clip("Stack" + seat + suffix);
            if (stack is null)
            {
                continue;
            }

            stack.GotoAndStop(21);
            stack.GetChild("Shadow")?.GotoAndStop(0);
        }

        Hide("Name" + seat);
        Hide("ChipTotal" + seat);
        Hide("ChipChange" + seat);
    }

    private void Hide(string name) => HideClip(Clip(name));

    private static void HideClip(IPubClip? clip)
    {
        if (clip is not null)
        {
            clip.Alpha = 0;
        }
    }

    private static void FadeOut(IPubClip? clip, double seconds, object? group = null)
    {
        if (clip is not null)
        {
            Fade.Out(clip, seconds, group);
        }
    }

    private static void FadeIn(IPubClip? clip, double seconds, object? group = null)
    {
        if (clip is not null)
        {
            Fade.In(clip, seconds, group);
        }
    }

    private static void SetIfPresent(IPubClip? parent, string child, string text)
    {
        IPubClip? field = parent?.GetChild(child);
        if (field is not null)
        {
            field.Text = text;
        }
    }
}
