using System;
using System.Collections.Generic;

using Refii.Titles.Fable2.PubGames.Framework;

namespace Refii.Titles.Fable2.PubGames.WizardsTower;

public sealed partial class WizardsTower : PubGame, IWizardsTowerView
{
    public const int BetColourAffordable = 13020042;
    public const int BetColourTooMuch = 16711680;

    public WizardsTower(IPubGameHost host, IPubTimer timer, IPubRandom random, IPubClip root)
        : base(host, timer, random)
    {
        Root = root;
        Chance = random;
        Sound = Audio;
        SetText.Loc = Localizer;
        ChipStack.Loc = Localizer;
    }

    public int Cheats { get; set; }

    public void SetPad(GamePad pad) => Pad = pad;

    public List<GameLogic> Games { get; private set; } = new();

    public GameLogic? VGame { get; private set; }

    public DeckOfCards? VDeck { get; private set; }

    public List<TowerPlayer> VPlayers { get; private set; } = new();

    public int RangeOfCards { get; set; } = 7;

    public int NumberOfCards { get; set; } = 8;

    public int NumberOfHeroes { get; set; } = 4;

    public int NumberOfRows { get; set; } = 8;

    public List<double> AIPlayTable { get; private set; } = new();

    public int LowAIBuyInMultiple { get; set; } = 1;

    public int HighAIBuyInMultiple { get; set; } = 1;

    public int NumberOfTables { get; set; } = 1;

    public List<int> StackedDeck { get; private set; } = new();

    public bool AutoJackpot { get; set; }

    public bool AutoBonus { get; set; }

    public bool AutoUltimate { get; set; }

    public int EffectiveBuyIn { get; set; } = 100;

    public bool NoSinglePlayerAIs { get; set; }

    public bool NoPlayerBets { get; set; }

    public double NPCStatusXScale { get; set; }

    public double NPCStatusYScale { get; set; }

    public bool TournamentOver { get; set; }

    public bool NoMoreHands { get; set; }

    public List<int> PresetAIList { get; private set; } = new();

    public double StickDelay { get; set; }

    public double PadDelay { get; set; }

    public bool NextHand { get; set; }

    public bool FrameStep { get; set; }

    public double SpeedFactor { get; set; } = 1;

    public double PlayerOutFactor { get; set; } = 1;

    public double HurryUpFactor { get; set; } = 1;

    public bool DelayedPlayHand { get; set; }

    public bool LTOverlayDialog { get; set; }

    public bool CashoutAlertDialog { get; set; }

    public int HandsSinceLastCashoutAlert { get; set; }

    public bool TutorialDialog { get; set; }

    public double GlintTime { get; set; }

    public double IdleTime { get; set; }

    public int SafetyFlip { get; set; }

    public bool LimitReached { get; set; }

    public bool TutorialHandOverHack { get; set; }

    public bool FinalTableAnims { get; set; }

    public int PlayerRank { get; set; } = 1;

    public int TournamentHandsPlayed { get; set; }

    public double DefaultDeckXPosition { get; set; } = 340;

    public double DefaultDeckYPosition { get; set; } = 860;

    public double DeckAngle { get; set; } = -30;

    public double DeckAngleVariation { get; set; } = 10;

    public double DeckPositionVariation { get; set; } = 10;

    public double CardSpeed { get; set; } = 150;

    public double XCenter { get; set; } = 900;

    public double YTowerTop { get; set; } = 200;

    public double YSpreadTop { get; set; } = 875;

    public double CardSpacing { get; set; } = 1.1;

    public bool ShowingDeck { get; set; } = true;

    public string YButtonText { get; set; } = "txtBanking";

    public bool ChipsFromBanking { get; set; }

    public double OfferInfoScale { get; set; }

    public bool HandOver { get; set; }

    public double RedTint { get; set; }

    public double GreenTint { get; set; }

    public double BlueTint { get; set; }

    public int CardsShown { get; set; } = 40;

    public bool TriggerUp { get; set; }

    public bool BankUp { get; set; }

    public bool ShowLSButton { get; set; }

    public List<double> BoardXPosition { get; private set; } = new();

    public List<double> BoardYPosition { get; private set; } = new();

    public List<double> ShowXPosition { get; private set; } = new();

    public List<double> ShowYPosition { get; private set; } = new();

    public List<double> ShowAngle { get; private set; } = new();

    public double DeckXPosition { get; set; }

    public double DeckYPosition { get; set; }

    public bool TutorialDialogActive { get; set; }

    public bool TutorialPauseUntilA { get; set; }

    public bool TutorialPauseUntilB { get; set; }

    public bool TutorialPauseUntilLS { get; set; }

    public bool TutorialIgnoreButtonPress { get; set; }

    public int TutorialStep { get; set; }

    public int TutorialLastStep { get; set; }

    public bool TutorialInputBlock { get; set; }

    public List<string> TutorialTextList { get; private set; } = new();

    public List<string> TutorialHeaderList { get; private set; } = new();

    public List<string> TutorialButtonA { get; private set; } = new();

    public List<string> TutorialButtonB { get; private set; } = new();

    public List<string> TutorialButtonLS { get; private set; } = new();

    public List<string> TutorialButtonLT { get; private set; } = new();

    private double _xPos;
    private double _yPos;
    private double _xShowPos;
    private double _yShowPos;
    private double _cardwidth;
    private double _cardheight;
    private int _rowPos;
    private int _rowSize;
    private double _scale;
    private int _t;
    private int _m;

    IReadOnlyList<int> IWizardsTowerView.PresetAIList => PresetAIList;

    IReadOnlyList<int> IWizardsTowerView.StackedDeck => StackedDeck;

    public IPubClip? Clip(string name) => Root?.GetChild(name);

    public void LocText(IPubClip? textfield, string? locID, Dictionary<string, string>? locVars = null)
    {
        if (textfield is null)
        {
            return;
        }

        if (locID is not null && locID.Length != 0)
        {
            Localizer.Localize(textfield, locID, locVars);
        }
        else if (locID is not null)
        {
            textfield.Text = locID;
        }
    }

    public string LocString(string? locID, Dictionary<string, string>? locVars = null) =>
        locID is not null && locID.Length != 0 ? Localizer.LocalizeString(locID, locVars) : string.Empty;

    public void IncrementSkillRating(int goldrisked)
    {
        if (Tutorial)
        {
            return;
        }

        PointsEarned(goldrisked);
    }

    public override bool EndGameWarning()
    {
        // THE TABLE IS NULL FOR THE FIRST FRAMES, and pressing Back is what reaches this.
        // `PostInitialize` ends in `ResetGameRules` -> `DisposeGameView`, which clears `VGame` and
        // empties `VPlayers`; `InitGameView` only fills them on a later `GameLoop` tick. The host
        // sets itself ready the moment `Run` returns, so there is a real window in which `Back`
        // arrives with nothing built — and the `VGame!` that used to stand here threw through
        // `EndGame`, taking the session and the player's stake with it.
        //
        // WHAT THE ORIGINAL DID IN THAT STATE, kept term for term: AS2 read `undefined.CurrentRow`,
        // which is neither 1 nor `Out`, so this half of the test was false and a cash game warned.
        // Answering "nothing is staked" instead would be the tidier reading and it is not the one
        // the shipped game gives.
        bool nothingDealt = VGame is not null && VPlayers.Count > 0 &&
            (VGame.CurrentRow == 1 || VPlayers[0].Out);

        if ((!Tournament && nothingDealt)
            || (Tournament && TournamentOver)
            || Tutorial)
        {
            return false;
        }

        return true;
    }

    public void UpdateBetAmountColor()
    {
        IPubClip? amount = Clip("BetInfo")?.GetChild("BetAmount");
        if (amount is null)
        {
            return;
        }

        bool affordable = VPlayers.Count > 0 && VPlayers[0].Stakes <= VPlayers[0].Chips;
        SetText.Color(amount, affordable ? BetColourAffordable : BetColourTooMuch);
    }

    public int CardToMovieClip(int card)
    {
        if (VGame!.BurnCardUsed && card == VDeck!.BurnedCard)
        {
            return 0;
        }

        if (VGame.BurnCardUsed && card == 0)
        {
            return VDeck!.BurnedCard;
        }

        return card;
    }

    public double DeckOffset(int index) =>
        index < CardsShown ? index / 2.0 : CardsShown / 2.0;

    public void ResetDeckDepths()
    {
        for (int i = VGame!.CurrentCard + 1; i < CardsShown; i++)
        {
            if (VGame.CheatUsed && i == VDeck!.TotalCards - 1)
            {
                continue;
            }

            Card(i).SwapDepths((10 * VDeck!.TotalCards) - (2 * i));
        }
    }

    public void ResetCardDepths()
    {
        for (int i = CardsShown - 1; i >= 0; i--)
        {
            Card(i).SwapDepths((10 * VDeck!.TotalCards) - (2 * i));
        }
    }

    public void SetPlayerSpeedFactor()
    {
        if (PlayerOutFactor >= 3)
        {
            return;
        }

        PlayerOutFactor *= 1.2;
        if (PlayerOutFactor > 3)
        {
            PlayerOutFactor = 3;
        }

        Action.SetGameSpeed(PlayerOutFactor * SpeedFactor * HurryUpFactor);
    }

    public void ClearPlayerSpeedFactor()
    {
        if (PlayerOutFactor == 1)
        {
            return;
        }

        PlayerOutFactor = 1;
        Action.SetGameSpeed(PlayerOutFactor * SpeedFactor * HurryUpFactor);
    }

    public void SetButtonText(string button, string buttontext)
    {
        if (Tutorial)
        {
            return;
        }

        if (button == "X"
            || (BankUp && button == "Y")
            || (TriggerUp && (button == "LT" || button == "RT")))
        {
            return;
        }

        if (button == "A")
        {
            if (buttontext.Length != 0)
            {
                SetButtonText("LT", "txtTowerOdds");
                SetButtonText("RT", Tournament ? "txtStandings" : "txtStats");
                TriggerUp = true;
            }
        }
        else if (button == "Y")
        {
            BankUp = true;
        }

        IPubClip? label = Clip(button + "ButtonText");
        IPubClip? icon = Clip(button + "button");
        if (label is null || icon is null)
        {
            return;
        }

        IPubClip? status = label.GetChild("StatusText");
        if (buttontext.Length == 0)
        {
            Fade.Out(label, 0.5, button + "button");
            Action.BlockOn(Fade.Out(icon, 0.5, button + "button"));
            if (status is not null)
            {
                SetText.LocReplace(status, buttontext, button + "button");
            }

            return;
        }

        Fade.In(label, 0.5, button + "button");
        Fade.In(icon, 0.5, button + "button");
        if (status is not null)
        {
            Action.BlockOn(SetText.LocReplace(status, buttontext, button + "button"));
        }
    }

    public void PressButtonAnimation(string button)
    {
        IPubClip? icon = Clip(button + "button");
        if (icon is null)
        {
            return;
        }

        Action.BlockOn(Scale.Up(icon, 1.2, 0.1, Action.StyleEaseIn, button));
        Action.BlockOn(Scale.Down(icon, 1.2, 0.1, Action.StyleEaseOut, button));
    }

    public void HideAllButtons(GameLogic? game = null)
    {
        if (game is not null && game != VGame)
        {
            return;
        }

        foreach (string name in new[]
        {
            "AButtonText", "Abutton", "BButtonText", "Bbutton", "YButtonText", "Ybutton",
            "LSbutton", "LSButtonText", "RTbutton", "RTButtonText", "LTbutton", "LTButtonText",
        })
        {
            IPubClip? clip = Clip(name);
            if (clip is not null)
            {
                Fade.Out(clip, 0.5);
            }
        }
    }

    public void RestoreAllButtons(GameLogic? game = null)
    {
        if ((game is not null && game != VGame) || Tutorial)
        {
            return;
        }

        // AS2 0x00ec29-0x00ed72: A, B and Y return only when their StatusText is not empty.
        foreach ((string text, string icon) in new[]
        {
            ("AButtonText", "Abutton"), ("BButtonText", "Bbutton"), ("YButtonText", "Ybutton"),
        })
        {
            IPubClip? label = Clip(text);
            IPubClip? button = Clip(icon);
            if (label?.GetChild("StatusText")?.Text is { Length: > 0 } && button is not null)
            {
                Fade.In(label, 0.5);
                Fade.In(button, 0.5);
            }
        }

        // AS2 0x00ed7c-0x00ee52: LS when ShowLSButton, then RT and LT unconditionally.
        if (ShowLSButton)
        {
            FadeInIfPresent("LSbutton");
            FadeInIfPresent("LSButtonText");
        }

        FadeInIfPresent("RTbutton");
        FadeInIfPresent("RTButtonText");
        FadeInIfPresent("LTbutton");
        FadeInIfPresent("LTButtonText");
    }

    private void FadeInIfPresent(string name)
    {
        if (Clip(name) is { } clip)
        {
            Fade.In(clip, 0.5);
        }
    }

    public override void BankingMenuDispelled()
    {
        Player? human = PlayerInfo.GetPlayer(0);
        if (human is not null)
        {
            VPlayers[0].ChipsWon = human.Chips - VPlayers[0].Chips;
            VPlayers[0].Chips = human.Chips;
        }

        ChipsFromBanking = true;
        UpdatePlayerInfo(VGame, 0);
        ChipsFromBanking = false;
        UpdateBetAmountColor();
        RestoreAllButtons();
    }

    public void CreateCardMovie(object? data)
    {
        if (data is not int c || VDeck is null)
        {
            return;
        }

        _rowSize = 1;
        _rowPos = 1;
        _xPos = XCenter - (_cardwidth * CardSpacing * (_rowSize / 2.0));
        _yPos = YTowerTop;

        for (var i = 0; i < c; i++)
        {
            if (_rowPos >= _rowSize)
            {
                _rowPos = 1;
                _rowSize++;
                _yPos += _cardheight / 2.8;
                _xPos = XCenter - (_cardwidth * CardSpacing * (_rowSize / 2.0));
            }
            else
            {
                _rowPos++;
                _xPos += _cardwidth * CardSpacing;
            }
        }

        _t = _rowSize;
        _m = _rowSize;

        if (_rowPos >= _rowSize && _rowSize > 1 && _rowSize <= VDeck.NumberOfRows)
        {
            IPubClip? box = Clip("TotalBoxes")?.AttachMovie("RowText", "Total" + _t, _t + 20);
            if (box is not null)
            {
                box.X = (float)(_xPos + (_cardwidth * CardSpacing) + (_cardwidth / 2));
                box.Y = (float)(_yPos - (_cardheight / 3) + (_cardheight / 2));
                box.Alpha = 0;
            }
        }

        if (_rowPos == 1 && _rowSize > 1 && _rowSize <= VDeck.NumberOfRows)
        {
            IPubClip? markers = Clip("Markers")?.AttachMovie("CashoutMarkers", "Markers" + _m, _m);
            if (markers is not null)
            {
                markers.X = (float)(_xPos - (_cardwidth * CardSpacing * 0.8) + (_cardwidth / 3));
                markers.Y = (float)(_yPos - (_cardheight / 3.5) + (_cardheight / 2));
                markers.GotoAndStop(1);
                for (var i = 1; i <= 5; i++)
                {
                    markers.GetChild("marker" + i)?.GotoAndStop(1);
                }
            }
        }

        Set(BoardXPosition, c, _xPos + (_cardwidth / 2));
        Set(BoardYPosition, c, _yPos + (_cardheight / 2));

        int numbered = VDeck.RangeOfCards * VDeck.NumberOfCards;
        if (c < numbered)
        {
            double angle = ((double)(c % VDeck.NumberOfCards) / (VDeck.NumberOfCards - 1)
                * (Math.PI / 2)) - (Math.PI / 4);
            Set(ShowXPosition, c,
                _xShowPos + (Math.Sin(angle) * (VDeck.NumberOfCards / 2.0) * _cardwidth));
            Set(ShowYPosition, c,
                _yShowPos - (Math.Cos(angle) * 1.25 * _cardheight)
                - (VDeck.RangeOfCards * _cardheight / 5)
                + (Math.Floor((double)c / VDeck.NumberOfCards) * _cardheight / 3));
            Set(ShowAngle, c, 180 * angle / Math.PI);
        }
        else
        {
            Set(ShowXPosition, c,
                _xShowPos - ((VDeck.NumberOfWarriors - 1) * _cardwidth * CardSpacing / 2)
                + (_cardwidth * CardSpacing * (c - numbered)));
            Set(ShowYPosition, c, _yShowPos);
            Set(ShowAngle, c, 0);
        }

        string symbol = c < numbered
            ? "Card" + (Math.Floor((double)c / VDeck.NumberOfCards) + 1)
            : "CardKnight";
        IPubClip? card = Cards.CardClips?.AttachMovie(
            symbol, "Card" + c, VDeck.TotalCards + (c * 2));
        if (card is not null)
        {
            card.X = (float)(ShowXPosition[c] + (Chance!.NextDouble() * 6) - 3);
            card.Y = (float)(ShowYPosition[c] + (Chance.NextDouble() * 6) - 3);
            card.XScale = (float)_scale;
            card.YScale = (float)_scale;
            card.X -= (float)(_cardwidth / 4);
            card.Y += (float)(_cardheight / 4);
            card.Rotation = (float)(ShowAngle[c] + (Chance.NextDouble() * 6) - 3);
        }

        CardClipData flags = Cards.Get("Card" + c);
        flags.Stacked = false;
        flags.Bonus = false;
        flags.Jackpot = false;

        if (c == VDeck.TotalCards - 1)
        {
            PreAction();
            Action.BlockOn(Action.RunOnce(this, PreChipStack));
            Action.BlockOn(Action.RunOnce(this, PreAnimate));
            Action.BlockOn(Action.RunOnce(this, PreTint));
            Action.BlockOn(Action.RunOnce(this, PreWait));
            Action.BlockOn(Action.RunOnce(this, PreFade));
            Action.BlockOn(Action.RunOnce(this, PreMoveTo));
            Action.BlockOn(Action.RunOnce(this, PrePlaySound));
            Action.BlockOn(Action.RunOnce(this, PreSweepCard));
            Action.BlockOn(Action.RunOnce(this, PreRotate));
            Action.BlockOn(Action.RunOnce(this, PreDealCard));
            Action.BlockOn(Action.RunOnce(this, PreScale));
            Action.BlockOn(Action.RunOnce(this, PreSetText));
            Action.BlockOn(Action.RunOnce(this, PreSwitchCard));
            Action.RunOnce(this, FinishGameViewInit);
        }
    }

    private static void Set(List<double> list, int index, double value)
    {
        while (list.Count <= index)
        {
            list.Add(0);
        }

        list[index] = value;
    }

    public void PreAction() => Action.Preallocate(25);

    public void PreChipStack() => ChipStack.Preallocate(25);

    public void PreAnimate() => Animate.Preallocate(50);

    public void PreTint() => Tint.Preallocate(10);

    public void PreWait() => Wait.Preallocate(20);

    public void PreFade() => Fade.Preallocate(50);

    public void PreMoveTo() => MoveTo.Preallocate(20);

    public void PrePlaySound() => PlaySound.Preallocate(25);

    public void PreSweepCard() => SweepCard.Preallocate(40);

    public void PreRotate() => Rotate.Preallocate(25);

    public void PreDealCard() => DealCard.Preallocate(40);

    public void PreScale() => Scale.Preallocate(20);

    public void PreSetText() => SetText.Preallocate(50);

    public void PreSwitchCard() => SwitchCard.Preallocate(50);
}
