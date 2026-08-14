using System;
using System.Collections.Generic;
using Refii.FlashGames.PubGames;

namespace Refii.FlashGames.Runtime;

public sealed class PubOverlays
{
    public const int OverlaysDepth = 100;
    public const int BankDepth = 17;
    public const int BankSubDepth = 18;
    public const int QuickTipsDepth = 20;
    public const int StandingsDepth = 21;
    public const int QuitDepth = 22;
    public const int CashOutDepth = 23;
    public const int HelpDepth = 24;
    public const int NeedChipsDepth = 25;
    public const double HelpNavDelay = 300;

    private readonly Keystone _keystone;
    private readonly IPubClip _root;
    private readonly IPubClip _overlays;

    private IPubClip? _bank;
    private IPubClip? _bankSub;
    private IPubClip? _quickTips;
    private IPubClip? _standings;
    private IPubClip? _quit;
    private IPubClip? _cashOut;
    private IPubClip? _help;
    private IPubClip? _needChips;
    private int _helpSection = 1;
    private int _helpSections;
    private double _helpWaitTicks;

    private BankOption _option = BankOption.QuickChips;
    private int _sliderDelta;
    private int _sliderMax;
    private double _waitTicks;
    private double _maxWaitTicks;
    private int _increment = 1;
    private int _sliderDir;

    public PubOverlays(Keystone keystone, IPubClip root)
    {
        _keystone = keystone;
        _root = root;
        _overlays = root.GetChild("overlays")
            ?? root.CreateEmptyMovieClip("overlays", OverlaysDepth);
        _overlays.SwapDepths(root.GetNextHighestDepth());
        Bank = new Banking(keystone);
    }

    public Banking Bank { get; }

    public bool BankShowing => _bank is not null;

    public bool BankSubShowing => _bankSub is not null;

    public bool QuickTipsShowing => _quickTips is not null;

    public bool StandingsShowing => _standings is not null;

    public bool QuitShowing => _quit is not null;

    public bool CashOutShowing => _cashOut is not null;

    public bool ExitRequested { get; private set; }

    public bool HelpShowing => _help is not null;

    public bool NeedChipsShowing => _needChips is not null;

    public int HelpSection => _helpSection;

    public int HelpSectionCount => _helpSections;

    public bool AbsorbingInput =>
        BankShowing || BankSubShowing || QuickTipsShowing || StandingsShowing ||
        QuitShowing || CashOutShowing || HelpShowing || NeedChipsShowing;

    public BankOption SelectedOption => _option;

    public int SliderValue => _sliderDelta;

    public void ToggleQuickTips()
    {
        if (_quickTips is not null)
        {
            _quickTips.RemoveMovieClip();
            _quickTips = null;
            return;
        }

        _quickTips = _overlays.AttachMovie("overlay_quicktips", "overlay_quicktips", QuickTipsDepth);
        if (_quickTips is null)
        {
            return;
        }

        InitQuickTips(_quickTips);
    }

    private void InitQuickTips(IPubClip clip)
    {
        Localize(clip.GetChild("body"), "txtOddsInfo", null);

        for (int i = 0; i < 8; i++)
        {
            Localize(clip.GetChild("value" + i), "txtOddsChip" + i, null);
        }

        for (int space = KeystoneSpace.FirstBetSpace; space <= KeystoneSpace.LastBetSpace; space++)
        {
            int payout = KeystoneRules.GetPayoutRate(space);
            int bet = _keystone.GetPayoutBet(space);
            Localize(
                clip.GetChild(KeystoneRules.BetIdToString(space) + "odds"),
                "txtPayout",
                new Dictionary<string, string>
                {
                    ["payout"] = payout.ToString(),
                    ["bet"] = bet.ToString(),
                    ["total"] = (payout + bet).ToString(),
                    ["normalized10"] = bet == 0
                        ? "0"
                        : ((payout + bet) * 10 / bet).ToString(),
                });
        }
    }

    public void ShowStandings(bool show)
    {
        if (show == (_standings is not null))
        {
            return;
        }

        if (!show)
        {
            _standings?.RemoveMovieClip();
            _standings = null;
            _keystone.PlaySound("G_fullscreenMenuCloses");
            return;
        }

        _standings = _overlays.AttachMovie("overlay_cashgame", "overlay_standings", StandingsDepth);
        if (_standings is null)
        {
            return;
        }

        InitStandings(_standings);
        UpdateStandings(_standings);
        _keystone.PlaySound("G_fullscreenMenuOpens");
    }

    private void InitStandings(IPubClip clip)
    {
        Player? human = _keystone.PlayerInfo.GetPlayer(0);
        if (human is null)
        {
            return;
        }

        Localize(clip.GetChild("title"), "txtCashGameTitle", null);
        Localize(
            clip.GetChild("subtitle"),
            "txtCashGameSubTitle",
            new Dictionary<string, string>
            {
                ["minBet"] = _keystone.MinBet.ToString(),
                ["maxBet"] = _keystone.MaxBet.ToString(),
            });

        Localize(
            clip.GetChild("playerName"),
            "txtCashGamePlayerName",
            new Dictionary<string, string> { ["name"] = human.Name });

        foreach ((string child, string key) in new[]
        {
            ("playsLabel", "txtCashGamePlaysLabel"),
            ("winningsLabel", "txtCashGameWinningsLabel"),
            ("borrowedLabel", "txtCashGameBorrowedLabel"),
            ("pointsEarnedLabel", "txtCashGamePointsEarnedLabel"),
            ("starRatingLabel", "txtCashGameStarRatingLabel"),
            ("pointsLabel", "txtCashGamePointsLabel"),
            ("nextLevelLabel", "txtCashGameNextLevelLabel"),
            ("chipsLabel", "txtCashGameChipsLabel"),
            ("goldLabel", "txtCashGameGoldLabel"),
            ("debtLabel", "txtCashGameDebtLabel"),
            ("sessionStats", "txtCashGameSessionStats"),
        })
        {
            Localize(clip.GetChild(child), key, null);
        }
    }

    private void UpdateStandings(IPubClip clip)
    {
        Player? human = _keystone.PlayerInfo.GetPlayer(0);
        if (human is null)
        {
            return;
        }

        Value(clip, "borrowedValue", "txtCashGameBorrowedValue", "chips", Bank.TotalBorrowed());
        Value(clip, "playsValue", "txtCashGamePlaysValue", "plays", _keystone.TotalRolls);
        Value(clip, "winningsValue", "txtCashGameWinningsValue", "gold", Bank.TotalWinnings());
        Value(
            clip, "pointsEarnedValue", "txtCashGamePointsEarnedValue",
            "points", human.SessionPointsEarned);
        Value(clip, "pointsValue", "txtCashGamePointsValue", "points", human.Points);
        Value(clip, "chipsValue", "txtCashGameChipsValue", "chips", human.Chips);
        Value(clip, "goldValue", "txtCashGameGoldValue", "gold", human.Gold);
        Value(clip, "debtValue", "txtCashGameDebtValue", "gold", human.Debt);

        if (human.StarRating == 5)
        {
            IPubClip? label = clip.GetChild("nextLevelLabel");
            IPubClip? value = clip.GetChild("nextLevelValue");
            if (label is not null)
            {
                label.Text = string.Empty;
            }

            if (value is not null)
            {
                value.Text = string.Empty;
            }
        }
        else if (human.StarRating < _keystone.StarPoints.Count)
        {
            Value(
                clip, "nextLevelValue", "txtCashGameNextLevelValue",
                "points", _keystone.StarPoints[human.StarRating] - human.Points);
        }

        clip.GetChild("starValue")?.GotoAndStop(human.StarRating);
    }

    private void Value(IPubClip clip, string child, string key, string variable, int amount)
    {
        Localize(
            clip.GetChild(child), key,
            new Dictionary<string, string> { [variable] = amount.ToString() });
    }

    public void ShowQuit()
    {
        if (_quit is not null)
        {
            return;
        }

        _quit = _overlays.AttachMovie("overlay_alert2", "overlay_quitgame", QuitDepth);
        if (_quit is null)
        {
            return;
        }

        Localize(
            _quit.GetChild("body"),
            _keystone.Tournament ? "txtQuitBodyTournament" : "txtQuitBodyCash", null);
        Localize(_quit.GetChild("button1")?.GetChild("label"), "txtQuitConfirm", null);
        Localize(_quit.GetChild("button2")?.GetChild("label"), "txtQuitCancel", null);
        _quit.GetChild("button1")?.GetChild("button")?.GotoAndStop("a");
        _quit.GetChild("button2")?.GetChild("button")?.GotoAndStop("b");
        _keystone.PlaySound("G_fullscreenMenuOpens");
    }

    private void CloseQuit(bool confirmed)
    {
        _quit?.RemoveMovieClip();
        _quit = null;
        _keystone.PlaySound("G_fullscreenMenuCloses");

        if (!confirmed)
        {
            return;
        }

        if (_keystone.Tournament || _keystone.Tutorial || !_keystone.BetsMade)
        {
            ExitRequested = true;
            return;
        }

        ShowCashOut();
    }

    public void ShowCashOut()
    {
        if (_cashOut is not null)
        {
            return;
        }

        Player? human = _keystone.PlayerInfo.GetPlayer(0);
        _cashOut = _overlays.AttachMovie("overlay_cashout", "overlay_cashout", CashOutDepth);
        if (_cashOut is null || human is null)
        {
            return;
        }

        Localize(_cashOut.GetChild("title"), "txtCashOutTitle", null);

        int winnings = Bank.TotalWinnings();
        string bodyKey = winnings < 0
            ? "txtCashOutDownGold"
            : winnings == 0 ? "txtCashOutEvenGold" : "txtCashOutUpGold";
        Localize(
            _cashOut.GetChild("body"), bodyKey,
            new Dictionary<string, string> { ["value"] = Math.Abs(winnings).ToString() });

        int gold = human.Gold;
        int debt = human.Debt - human.Chips;
        if (debt < 0)
        {
            gold -= debt;
            debt = 0;
        }

        Localize(
            _cashOut.GetChild("line0"), "txtCashOutGold",
            new Dictionary<string, string> { ["value"] = gold.ToString() });
        Localize(
            _cashOut.GetChild("line1"), "txtCashOutDebt",
            new Dictionary<string, string> { ["value"] = debt.ToString() });

        IPubClip? points = _cashOut.GetChild("PointsText");
        if (human.StarRating == 5)
        {
            if (points is not null)
            {
                points.Text = string.Empty;
            }
        }
        else if (human.StarRating < _keystone.StarPoints.Count)
        {
            Localize(
                points, "txtCashOutPointsToNextLevel",
                new Dictionary<string, string>
                {
                    ["value"] = (_keystone.StarPoints[human.StarRating] - human.Points).ToString(),
                });
        }

        _cashOut.GetChild("StarRating")?.GotoAndStop(human.StarRating);
        Localize(_cashOut.GetChild("button1")?.GetChild("label"), "txtCashOutConfirm", null);
        _cashOut.GetChild("button1")?.GetChild("button")?.GotoAndStop("a");
        Localize(_cashOut.GetChild("tip"), "txtCashOutHint" + _keystone.Random.Range(10), null);

        _keystone.PlaySound("G_fullscreenMenuOpens");
    }

    private void CloseCashOut()
    {
        _cashOut?.RemoveMovieClip();
        _cashOut = null;
        _keystone.PlaySound("G_fullscreenMenuCloses");
        _keystone.GameOver();
        ExitRequested = true;
    }

    public void ShowHelp()
    {
        if (_help is not null)
        {
            return;
        }

        _help = _overlays.AttachMovie("overlay_help", "overlay_help", HelpDepth);
        if (_help is null)
        {
            return;
        }

        _helpWaitTicks = 0;
        _helpSection = 1;
        _helpSections = 0;
        while (_keystone.Localizer.Contains("txtGameHelpText" + (_helpSections + 1)))
        {
            _helpSections++;
        }

        Localize(_help.GetChild("title"), "txtGameHelpTitle", null);
        Localize(_help.GetChild("button1")?.GetChild("label"), "txtGameHelpDone", null);
        _help.GetChild("button1")?.GetChild("button")?.GotoAndStop("b");
        UpdateHelpSection();
        _keystone.PlaySound("G_fullscreenMenuOpens");
    }

    private void UpdateHelpSection()
    {
        if (_help is null)
        {
            return;
        }

        Localize(_help.GetChild("body"), "txtGameHelpText" + _helpSection, null);
        Localize(_help.GetChild("section"), "txtGameHelpSection" + _helpSection, null);
        _help.GetChild("image")?.GotoAndStop(_helpSection);

        IPubClip? left = _help.GetChild("leftArrow");
        if (left is not null)
        {
            left.Alpha = _helpSection <= 1 ? 0 : 100;
        }

        IPubClip? right = _help.GetChild("rightArrow");
        if (right is not null)
        {
            right.Alpha = _helpSection >= _helpSections ? 0 : 100;
        }
    }

    public void ChangeHelpSection(int delta)
    {
        int next = Math.Clamp(_helpSection + delta, 1, Math.Max(_helpSections, 1));
        if (next == _helpSection)
        {
            return;
        }

        _helpSection = next;
        UpdateHelpSection();
        _keystone.PlaySound("G_fullscreenMenuOpens");
    }

    private void CloseHelp()
    {
        _help?.RemoveMovieClip();
        _help = null;
        _keystone.PlaySound("G_fullscreenMenuCloses");
    }

    public void ShowNeedChips()
    {
        if (_needChips is not null)
        {
            return;
        }

        string export = _keystone.Tournament ? "overlay_alert1" : "overlay_alert2";
        _needChips = _overlays.AttachMovie(export, "overlay_needchips", NeedChipsDepth);
        if (_needChips is null)
        {
            return;
        }

        IPubClip? body2 = _needChips.GetChild("body2");
        if (body2 is not null)
        {
            body2.Text = string.Empty;
        }

        if (_keystone.Tournament)
        {
            Localize(_needChips.GetChild("body"), "txtNeedChipsBodyTournament", null);
            Localize(_needChips.GetChild("button1")?.GetChild("label"), "txtNeedChipsOK", null);
            _needChips.GetChild("button1")?.GetChild("button")?.GotoAndStop("a");
        }
        else
        {
            Localize(_needChips.GetChild("body"), "txtNeedChipsBody", null);
            Localize(_needChips.GetChild("button1")?.GetChild("label"), "txtNeedChipsYes", null);
            _needChips.GetChild("button1")?.GetChild("button")?.GotoAndStop("a");
            Localize(_needChips.GetChild("button2")?.GetChild("label"), "txtNeedChipsCancel", null);
            _needChips.GetChild("button2")?.GetChild("button")?.GotoAndStop("b");
        }

        _keystone.PlaySound("G_fullscreenMenuOpens");
    }

    private void CloseNeedChips(bool toBank)
    {
        _needChips?.RemoveMovieClip();
        _needChips = null;
        _keystone.PlaySound("G_fullscreenMenuCloses");

        if (toBank && !_keystone.Tournament)
        {
            OpenBank();
        }
    }

    public void OpenBank()
    {
        if (_bank is not null)
        {
            return;
        }

        _bank = _overlays.AttachMovie("overlay_bank", "overlay_bank", BankDepth);
        if (_bank is null)
        {
            return;
        }

        _waitTicks = 0;
        _maxWaitTicks = Banking.UpDownNavDelay1;

        if (!Bank.CasualGame)
        {
            _option = BankOption.CashOut;
        }
        else if (_option < BankOption.QuickChips || _option > BankOption.CashOut)
        {
            _option = BankOption.QuickChips;
        }

        Localize(_bank.GetChild("title"), "txtBankTitle", null);
        Localize(
            _bank.GetChild("line0")?.GetChild("itemtext"),
            "txtBankQuickChips",
            new Dictionary<string, string> { ["chips"] = Bank.QuickChipsAmount().ToString() });
        Localize(_bank.GetChild("line1")?.GetChild("itemtext"), "txtBankGetChips", null);
        Localize(_bank.GetChild("line2")?.GetChild("itemtext"), "txtBankPayDebtWithGold", null);
        Localize(_bank.GetChild("line3")?.GetChild("itemtext"), "txtBankCashOut", null);
        Localize(_bank.GetChild("button1")?.GetChild("label"), "txtBankSelect", null);
        Localize(_bank.GetChild("button2")?.GetChild("label"), "txtBankCancel", null);
        _bank.GetChild("button1")?.GetChild("button")?.GotoAndStop("a");
        _bank.GetChild("button2")?.GetChild("button")?.GotoAndStop("b");

        RefreshBankSelection();
        _keystone.PlaySound("G_fullscreenMenuOpens");
    }

    private void RefreshBankSelection()
    {
        if (_bank is null)
        {
            return;
        }

        for (int i = 0; i <= (int)Banking.LastOption; i++)
        {
            IPubClip? select = _bank.GetChild("select" + i);
            if (select is not null)
            {
                select.Alpha = i == (int)_option ? 100 : 0;
            }

            IPubClip? line = _bank.GetChild("line" + i);
            if (line is not null)
            {
                line.Alpha = Bank.IsEnabled((BankOption)i) ? 100 : 50;
            }
        }
    }

    public void SelectOption(BankOption option)
    {
        _option = option;
        RefreshBankSelection();
    }

    public void OpenSub() => OpenBankSub();

    public void SetSlider(int value)
    {
        _sliderDelta = Math.Clamp(value, 0, _sliderMax);
        RefreshBankSub();
    }

    public void ConfirmSub()
    {
        if (_sliderDelta > 0)
        {
            Bank.Commit(_option, _sliderDelta);
        }

        CloseBankSub();
    }

    public void CloseBank()
    {
        _bank?.RemoveMovieClip();
        _bank = null;
        _keystone.PlaySound("G_fullscreenMenuCloses");
    }

    public void Update(double ticks, GamePad? pad)
    {
        if (pad is null)
        {
            return;
        }

        if (_needChips is not null)
        {
            if (pad.BtnAPressed)
            {
                CloseNeedChips(true);
            }
            else if (pad.BtnBPressed)
            {
                CloseNeedChips(false);
            }

            return;
        }

        if (_help is not null)
        {
            if (pad.BtnBPressed || pad.BackPressed)
            {
                CloseHelp();
                return;
            }

            int step = 0;
            if (pad.LeftThumbRight || pad.DPadRight)
            {
                step += 1;
            }

            if (pad.LeftThumbLeft || pad.DPadLeft)
            {
                step -= 1;
            }

            if (step == 0)
            {
                _helpWaitTicks = 0;
            }
            else if (_helpWaitTicks <= 0)
            {
                _helpWaitTicks = HelpNavDelay;
                ChangeHelpSection(step);
                _keystone.PlaySound("G_moveSelector");
            }
            else
            {
                _helpWaitTicks -= ticks;
            }

            return;
        }

        if (_cashOut is not null)
        {
            if (pad.BtnAPressed)
            {
                CloseCashOut();
            }

            return;
        }

        if (_quit is not null)
        {
            if (pad.BtnAPressed)
            {
                CloseQuit(true);
            }
            else if (pad.BtnBPressed)
            {
                CloseQuit(false);
            }

            return;
        }

        if (_standings is not null)
        {
            UpdateStandings(_standings);
            if (pad.RightTrigger < GamePad.TriggerThreshold || pad.BtnBPressed)
            {
                ShowStandings(false);
            }

            return;
        }

        if (_quickTips is not null)
        {
            if (pad.LeftTrigger < GamePad.TriggerThreshold || pad.BtnBPressed)
            {
                ToggleQuickTips();
            }

            return;
        }

        if (_bankSub is not null)
        {
            UpdateBankSub(ticks, pad);
            return;
        }

        if (_bank is not null)
        {
            UpdateBank(ticks, pad);
        }
    }

    private void UpdateBank(double ticks, GamePad pad)
    {
        if (pad.BtnBPressed)
        {
            CloseBank();
            return;
        }

        if (pad.BtnAPressed)
        {
            if (!Bank.IsEnabled(_option))
            {
                _keystone.PlaySound("G_error");
                return;
            }

            if (_option is BankOption.GetChips or BankOption.PayDebtWithGold)
            {
                OpenBankSub();
                return;
            }

            if (_option == BankOption.QuickChips)
            {
                Bank.Commit(BankOption.QuickChips, 0);
            }

            CloseBank();
            return;
        }

        int direction = 0;
        if (pad.LeftThumbUp || pad.DPadUp)
        {
            direction -= 1;
        }

        if (pad.LeftThumbDown || pad.DPadDown)
        {
            direction += 1;
        }

        if (direction == 0)
        {
            _waitTicks = 0;
            _maxWaitTicks = Banking.UpDownNavDelay1;
            return;
        }

        if (_waitTicks > 0)
        {
            _waitTicks -= ticks;
            return;
        }

        _waitTicks = _maxWaitTicks;
        _maxWaitTicks = Banking.UpDownNavDelay2;
        _option = Banking.Step(_option, direction);
        _keystone.PlaySound("G_moveSelector");
        RefreshBankSelection();
    }

    private void OpenBankSub()
    {
        _bank?.RemoveMovieClip();
        _bank = null;

        _bankSub = _overlays.AttachMovie("overlay_banksub", "overlay_banksub", BankSubDepth);
        if (_bankSub is null)
        {
            return;
        }

        _sliderDir = 0;
        _sliderDelta = 0;
        _waitTicks = 0;
        _maxWaitTicks = Banking.LeftRightNavDelay1;
        _increment = 1;
        _sliderMax = Bank.SliderMax(_option);

        IPubClip? thumb0 = _bankSub.GetChild("thumb0");
        IPubClip? thumb1 = _bankSub.GetChild("thumb1");
        if (thumb0 is not null)
        {
            thumb0.Visible = false;
        }

        if (thumb1 is not null)
        {
            thumb1.Visible = false;
        }

        Localize(_bankSub.GetChild("button1")?.GetChild("label"), "txtBankConfirm", null);
        Localize(_bankSub.GetChild("button2")?.GetChild("label"), "txtBankCancel", null);
        _bankSub.GetChild("button1")?.GetChild("button")?.GotoAndStop("a");
        _bankSub.GetChild("button2")?.GetChild("button")?.GotoAndStop("b");
        Localize(_bankSub.GetChild("line1label"), "txtBankDebtLabel", null);

        bool borrowing = _option == BankOption.GetChips;
        Localize(
            _bankSub.GetChild("sliderlabel"),
            borrowing ? "txtBorrowSliderLabel" : "txtDebtSliderLabel", null);
        Localize(
            _bankSub.GetChild("line0label"),
            borrowing ? "txtBankChipsLabel" : "txtBankGoldLabel", null);
        Localize(
            _bankSub.GetChild("title"),
            borrowing ? "txtBankGetChipsTitle" : "txtBankPayDebtWithGoldTitle", null);
        Localize(
            _bankSub.GetChild("instructions"),
            borrowing ? "txtBankGetChipsInstructions" : "txtBankPayDebtWithGoldInstructions", null);
        Localize(
            _bankSub.GetChild("tip"),
            borrowing ? "txtBankGetChipsTip" : "txtBankPayDebtWithGoldTip", null);

        RefreshBankSub();
        _keystone.PlaySound("G_fullscreenMenuOpens");
    }

    private void RefreshBankSub()
    {
        Player? human = _keystone.PlayerInfo.GetPlayer(0);
        if (_bankSub is null || human is null)
        {
            return;
        }

        bool borrowing = _option == BankOption.GetChips;
        Localize(
            _bankSub.GetChild("line0"),
            borrowing ? "txtBankChipsValue" : "txtBankGoldValue",
            new Dictionary<string, string>
            {
                ["value"] = borrowing
                    ? (human.Chips + _sliderDelta).ToString()
                    : (human.Gold - _sliderDelta).ToString(),
            });

        Localize(
            _bankSub.GetChild("line1"),
            "txtBankDebtValue",
            new Dictionary<string, string>
            {
                ["value"] = borrowing
                    ? (human.Debt + _sliderDelta).ToString()
                    : (human.Debt - _sliderDelta).ToString(),
            });

        Localize(
            _bankSub.GetChild("slidervalue"),
            "txtSliderValue",
            new Dictionary<string, string> { ["value"] = _sliderDelta.ToString() });

        IPubClip? thumb = _bankSub.GetChild("thumb");
        IPubClip? thumb0 = _bankSub.GetChild("thumb0");
        IPubClip? thumb1 = _bankSub.GetChild("thumb1");
        if (thumb is not null && thumb0 is not null && thumb1 is not null)
        {
            thumb.X = ((thumb1.X - thumb0.X) * _sliderDelta / Math.Max(_sliderMax, 1)) + thumb0.X;
        }
    }

    private void UpdateBankSub(double ticks, GamePad pad)
    {
        if (pad.BtnBPressed)
        {
            CloseBankSub();
            return;
        }

        if (pad.BtnAPressed)
        {
            if (_sliderDelta > 0)
            {
                Bank.Commit(_option, _sliderDelta);
            }

            CloseBankSub();
            return;
        }

        int direction = 0;
        if (pad.LeftThumbLeft || pad.DPadLeft)
        {
            direction -= 1;
        }

        if (pad.LeftThumbRight || pad.DPadRight)
        {
            direction += 1;
        }

        if (direction == 0 || _sliderDir == -direction)
        {
            _waitTicks = 0;
            _maxWaitTicks = Banking.LeftRightNavDelay1;
            _increment = 1;
        }
        else if (_waitTicks <= 0)
        {
            int before = _sliderDelta;
            _sliderDelta += direction * _increment;
            if (_sliderDelta < 0)
            {
                _sliderDelta = 0;
            }
            else if (_sliderDelta > _sliderMax)
            {
                _sliderDelta = _sliderMax;
            }
            else if (_sliderDelta != before)
            {
                _keystone.PlaySound("G_sliderUp");
            }

            _waitTicks = _maxWaitTicks;
            _maxWaitTicks /= 2;
            if (_maxWaitTicks < 30)
            {
                _increment += (int)Math.Floor(Math.Max(_increment * 0.25, 1));
            }

            RefreshBankSub();
        }
        else
        {
            _waitTicks -= ticks;
        }

        _sliderDir = direction;
    }

    private void CloseBankSub()
    {
        _bankSub?.RemoveMovieClip();
        _bankSub = null;
        _keystone.PlaySound("G_fullscreenMenuCloses");
    }

    private void Localize(
        IPubClip? clip, string key, IReadOnlyDictionary<string, string>? variables)
    {
        if (clip is not null)
        {
            clip.Text = _keystone.Localizer.LocalizeString(key, variables);
        }
    }
}
