using System;
using System.Collections.Generic;
using System.Globalization;

using Refii.FlashGames.Framework;

namespace Refii.FlashGames.WizardsTower;

public sealed partial class WizardsTower
{
    private static Dictionary<string, string> Vars(string key, object value) =>
        new() { [key] = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty };

    private static Dictionary<string, string> Vars(
        string key1, object value1, string key2, object value2) =>
        new()
        {
            [key1] = Convert.ToString(value1, CultureInfo.InvariantCulture) ?? string.Empty,
            [key2] = Convert.ToString(value2, CultureInfo.InvariantCulture) ?? string.Empty,
        };

    private bool NotMyTable(GameLogic? game) => game is not null && game != VGame;

    private static IPubClip Card(int index) => Cards.Ref("Card" + index);

    private IPubClip? OfferText(string field) =>
        Clip("OfferInfo")?.GetChild("TextClip")?.GetChild(field);

    public void UpdateChipsForPlayer(TowerPlayer towerplayer, int? dontAnimatePlayerPosition = null)
    {
        Player? info = PlayerInfo.GetPlayer(towerplayer.ID);
        if (info is null)
        {
            return;
        }

        towerplayer.ChipsWon = info.Chips;
        towerplayer.Chips = info.Chips;
        if (dontAnimatePlayerPosition is not null)
        {
            towerplayer.ChipsWon = 0;
            LocText(
                Clip("ChipTotal" + dontAnimatePlayerPosition)?.GetChild("TextField"),
                "txtChipTotal", Vars("CHIPS", towerplayer.Chips));
        }
    }

    public void SetChipsForPlayer(
        TowerPlayer towerplayer, double chips, int? dontAnimatePlayerPosition = null)
    {
        towerplayer.ChipsWon = (int)chips;
        towerplayer.Chips = (int)chips;
        if (dontAnimatePlayerPosition is not null)
        {
            towerplayer.ChipsWon = 0;
            LocText(
                Clip("ChipTotal" + dontAnimatePlayerPosition)?.GetChild("TextField"),
                "txtChipTotal", Vars("CHIPS", towerplayer.Chips));
        }

        Player? info = PlayerInfo.GetPlayer(towerplayer.ID);
        if (info is not null)
        {
            info.Chips = (int)chips;
        }
    }

    public void BuyChipsForPlayer(
        TowerPlayer towerplayer, double chips, int? dontAnimatePlayerPosition = null)
    {
        towerplayer.ChipsWon = (int)chips;
        towerplayer.Chips += (int)chips;
        if (dontAnimatePlayerPosition is not null)
        {
            towerplayer.ChipsWon = 0;
            LocText(
                Clip("ChipTotal" + dontAnimatePlayerPosition)?.GetChild("TextField"),
                "txtChipTotal", Vars("CHIPS", towerplayer.Chips));
        }

        if (Tutorial)
        {
            return;
        }

        Player? info = PlayerInfo.GetPlayer(towerplayer.ID);
        if (info is null)
        {
            return;
        }

        info.Chips += (int)chips;
        if (!Tournament)
        {
            info.Debt += (int)chips;
        }
    }

    public void CashoutChipsForPlayer(
        TowerPlayer towerplayer, double chips, int? dontAnimatePlayerPosition = null)
    {
        if (chips > towerplayer.Chips)
        {
            chips = towerplayer.Chips;
        }

        towerplayer.Chips -= (int)chips;
        if (dontAnimatePlayerPosition is not null)
        {
            towerplayer.ChipsWon = 0;
            LocText(
                Clip("ChipTotal" + dontAnimatePlayerPosition)?.GetChild("TextField"),
                "txtChipTotal", Vars("CHIPS", towerplayer.Chips));
        }

        Player? info = PlayerInfo.GetPlayer(towerplayer.ID);
        if (info is null)
        {
            return;
        }

        info.Chips -= (int)chips;
        info.Debt -= (int)chips;
    }

    public void WinChipsForPlayer(TowerPlayer towerplayer, double chips)
    {
        towerplayer.ChipsWon = (int)chips;
        towerplayer.Chips += (int)chips;
        towerplayer.NetChips += (int)chips;
        if (Tutorial)
        {
            return;
        }

        Player? info = PlayerInfo.GetPlayer(towerplayer.ID);
        if (info is not null)
        {
            info.Chips += (int)chips;
        }
    }

    public void BetChipsForPlayer(TowerPlayer towerplayer, double chips)
    {
        if (chips > towerplayer.Chips)
        {
            chips = towerplayer.Chips;
        }

        towerplayer.Chips -= (int)chips;
        towerplayer.NetChips -= (int)chips;
        if (Tutorial)
        {
            return;
        }

        Player? info = PlayerInfo.GetPlayer(towerplayer.ID);
        if (info is not null)
        {
            info.Chips -= (int)chips;
        }
    }

    public void AchievementEarnedWT(GameLogic? game, string achievementname)
    {
        if (NotMyTable(game) || Tutorial)
        {
            return;
        }

        AchievementEarned(achievementname);
    }

    public void UpdateBonusState(GameLogic? game = null)
    {
        if (NotMyTable(game) || VGame is null || VDeck is null)
        {
            return;
        }

        IPubClip? totals = Clip("TotalBoxes");
        IPubClip? bustedX = Clip("BustedRowX");

        if (VGame.CurrentRow == 1)
        {
            double delay = 0;
            for (int row = VDeck.NumberOfRows; row >= 2; row--)
            {
                IPubClip? box = totals?.GetChild("Total" + row);
                if (box is not null)
                {
                    Action.Delay(Fade.Out(box, 0.5), delay);
                    if (bustedX is not null && box.Y == bustedX.Y)
                    {
                        Action.Delay(Fade.Out(bustedX, 0.5), delay);
                    }
                }

                delay += 0.2;
            }

            IPubClip? offer = Clip("OfferInfo");
            if (offer is not null)
            {
                Fade.Out(offer, 0.5, "OfferGroup");
            }

            return;
        }

        int value = VDeck.RowValue(VGame.CurrentRow);
        int bonus = VDeck.TotalBonus(VGame.CurrentRow);
        double stake = 1;
        if (VPlayers[0].Stakes != 0)
        {
            stake = VPlayers[0].SafetyStakes / 15.0;
        }

        IPubClip? here = totals?.GetChild("Total" + VGame.CurrentRow);
        IPubClip? below = totals?.GetChild("Total" + (VGame.CurrentRow - 1));
        bool busted = VDeck.RowIsBusted(VGame.CurrentRow);

        if (here is not null)
        {
            IPubClip? status = here.GetChild("StatusText");
            if (busted)
            {
                if (status is not null)
                {
                    SetText.Replace(status, string.Empty);
                }

                if (bustedX is not null)
                {
                    MoveTo.Instant(bustedX, here.X + 50, here.Y);
                    Fade.In(bustedX, 0.5);
                }
            }
            else if (status is not null)
            {
                SetText.LocWithVars(status, "txtStatusAmount", Vars("CHIPS", value));
            }

            Fade.In(here, 0.5);
        }

        if (VGame.CurrentRow > 2 && below is not null)
        {
            IPubClip? status = below.GetChild("StatusText");
            if (status is not null)
            {
                SetText.LocWithVars(
                    status, "txtStatusAmount", Vars("CHIPS", VDeck.RowValue(VGame.CurrentRow - 1)));
            }

            Fade.Exact(below, 40, 0.5);
        }

        IPubClip? pays = OfferText("PaysText");
        IPubClip? middle = OfferText("TextMiddle");
        IPubClip? offerText = OfferText("OfferText");
        IPubClip? offerBusted = Clip("OfferInfo")?.GetChild("BustedX");

        if (busted)
        {
            if (pays is not null)
            {
                SetText.Replace(pays, string.Empty, "OfferGroup");
            }

            if (middle is not null)
            {
                SetText.Replace(middle, string.Empty, "OfferGroup");
            }

            if (offerText is not null)
            {
                SetText.Replace(offerText, string.Empty, "OfferGroup");
            }

            if (offerBusted is not null)
            {
                Fade.InstantIn(offerBusted, "OfferGroup");
            }

            return;
        }

        if (pays is not null)
        {
            if (bonus == 1)
            {
                SetText.LocWithVars(pays, "txtTotalPays", Vars("TOTAL", value), "OfferGroup");
            }
            else
            {
                SetText.LocWithVars(
                    pays, "txtBonusPays", Vars("TOTAL", value, "BONUS", bonus), "OfferGroup");
            }
        }

        if (offerText is not null)
        {
            SetText.LocWithVars(
                offerText, "txtOfferAmount", Vars("CHIPS", value * bonus * stake), "OfferGroup");
        }

        if (middle is not null)
        {
            SetText.Replace(middle, string.Empty, "OfferGroup");
        }

        if (offerBusted is not null)
        {
            Fade.InstantOut(offerBusted, "OfferGroup");
        }
    }

    public void StartAnimateOfferInfo()
    {
        foreach (string field in new[] { "PaysText", "OfferText", "TextMiddle" })
        {
            IPubClip? clip = OfferText(field);
            if (clip is not null)
            {
                SetText.Replace(clip, string.Empty);
            }
        }

        IPubClip? busted = Clip("OfferInfo")?.GetChild("BustedX");
        if (busted is not null)
        {
            Fade.InstantOut(busted, "OfferGroup");
        }
    }

    public void AnimateOfferInfo(int total)
    {
        int bonus = VDeck!.TotalBonus(VGame!.CurrentRow - 1);
        IPubClip? middle = OfferText("TextMiddle");
        IPubClip? busted = Clip("OfferInfo")?.GetChild("BustedX");

        if (total < 0)
        {
            if (middle is not null)
            {
                SetText.Replace(middle, string.Empty);
            }

            if (busted is not null)
            {
                Fade.InstantIn(busted, "OfferGroup");
            }

            return;
        }

        if (middle is not null)
        {
            SetText.LocWithVars(
                middle, "txtOfferAmount",
                Vars("CHIPS", bonus == 1 ? total.ToString(CultureInfo.InvariantCulture)
                    : total + "x" + bonus));
        }

        if (busted is not null)
        {
            Fade.InstantOut(busted, "OfferGroup");
        }
    }

    public void EndAnimateOfferInfo()
    {
        int value = VDeck!.RowValue(VGame!.CurrentRow);
        int bonus = VDeck.TotalBonus(VGame.CurrentRow);
        double stake = 1;
        if (VPlayers[0].Stakes != 0)
        {
            stake = VPlayers[0].SafetyStakes / 15.0;
        }

        Wait.Seconds(0);
        Wait.Seconds(0.5, "OfferGroup");

        if (VDeck.RowIsBusted(VGame.CurrentRow))
        {
            return;
        }

        IPubClip? text = Clip("OfferInfo")?.GetChild("TextClip");
        if (text is null)
        {
            return;
        }

        OfferInfoScale = text.XScale;
        Action.BlockOn(Scale.Thin(text, 100, 0.3, Action.StyleEaseOut, "OfferGroup"));

        IPubClip? pays = OfferText("PaysText");
        IPubClip? offer = OfferText("OfferText");
        IPubClip? middle = OfferText("TextMiddle");

        if (pays is not null)
        {
            if (bonus == 1)
            {
                SetText.LocWithVars(pays, "txtTotalPays", Vars("TOTAL", value), "OfferGroup");
            }
            else
            {
                SetText.LocWithVars(
                    pays, "txtBonusPays", Vars("TOTAL", value, "BONUS", bonus), "OfferGroup");
            }
        }

        if (offer is not null)
        {
            SetText.LocWithVars(
                offer, "txtOfferAmount", Vars("CHIPS", value * bonus * stake), "OfferGroup");
        }

        if (middle is not null)
        {
            SetText.Replace(middle, string.Empty, "OfferGroup");
        }

        Action.BlockOn(Scale.Thick(text, 100, 0.3, Action.StyleEaseIn, "OfferGroup"));

        IPubClip? animating = Clip("OfferAnimating")?.GetChild("OfferText");
        if (animating is not null)
        {
            SetText.LocWithVars(
                animating, "txtOfferAmount", Vars("CHIPS", value * bonus * stake), "OfferGroup");
        }

        Wait.UntilFinished("OfferGroup");
    }

    public void UpdatePlayerChipInfo(int player)
    {
        if (((Tutorial || NoSinglePlayerAIs) && player > 0) || player >= VPlayers.Count)
        {
            return;
        }

        IPubClip? field = Clip("ChipTotal" + player)?.GetChild("TextField");
        if (field is null)
        {
            return;
        }

        string group = "UpdateChipTotal" + player;
        Action.BlockOn(ChipsFromBanking
            ? ChipStack.NoApplause(field, player, VPlayers[player].Chips, VPlayers[player].Stakes, group)
            : ChipStack.Timed(field, player, VPlayers[player].Chips, VPlayers[player].Stakes, group));
    }

    public void UpdatePlayerStatusInfo(int player)
    {
        if (player >= VPlayers.Count || VPlayers[player].SeatEmpty || VGame is null || VDeck is null)
        {
            return;
        }

        IPubClip? status = Clip("Status" + player);
        if (status is null)
        {
            return;
        }

        double amount;
        string key;
        if (VGame.CurrentRow == 1)
        {
            amount = VPlayers[player].Stakes;
            key = "txtBetChips";
        }
        else
        {
            amount = VPlayers[player].Out
                ? Math.Abs(VPlayers[player].ChipsWon)
                : VPlayers[player].SafetyStakes;
            key = "txtWonChips";
        }

        if (player == 0)
        {
            key = string.Empty;
        }

        if (VDeck.RowIsBusted(VGame.CurrentRow))
        {
            if (VPlayers[player].In)
            {
                key = player == 0 ? string.Empty : "txtNPCBusted";
            }
            else if (!VPlayers[player].Out)
            {
                key = player == 0 ? string.Empty : "txtBetChips";
            }
        }
        else if (VPlayers[player].In)
        {
            if (player == 0)
            {
                key = string.Empty;
            }
            else if (VGame.CurrentRow != 1)
            {
                key = "txtBetChips";
            }
        }
        else if (!VPlayers[player].Out)
        {
            if (player == 0 && VGame.Jackpot)
            {
                key = "txtPlayerJackpot";
            }
            else if (player == 0 && VDeck.RowBonus(VGame.CurrentRow) > 1)
            {
                key = "txtPlayerBonus";
            }
            else if (player == 0)
            {
                key = string.Empty;
            }
            else
            {
                key = VGame.CurrentRow != 1 ? "txtBetChips" : "txtNPCDeal";
            }
        }

        if (HandOver && player != 0)
        {
            key = "txtNPCDeal";
        }

        string line = key == "txtPlayerBonus"
            ? LocString(key, Vars("MULT", VDeck.RowBonus(VGame.CurrentRow)))
            : LocString(key, Vars("CHIPS", amount));

        if (VPlayers[player].StatusText == line)
        {
            return;
        }

        IPubClip? statusText = status.GetChild("StatusText");
        IPubClip? bustedX = status.GetChild("BustedX");
        IPubClip? textAnimation = Clip("TextAnimation");

        if (player == 0 && key.Length != 0)
        {
            double fit = 1;
            if (line.Length > 8)
            {
                fit = 8.0 / line.Length;
            }

            if (key == "txtPlayerBonus")
            {
                Scale.Instant(status, 1.5 * fit);
                if (textAnimation is not null)
                {
                    Scale.Instant(textAnimation, 0.75);
                }
            }
            else
            {
                Scale.Instant(status, 2 * fit);
            }

            MoveTo.Instant(status, 900, 650);
            if (statusText is not null)
            {
                SetText.Replace(statusText, line);
                SetText.Color(statusText, 16777215);
            }

            if (textAnimation is not null)
            {
                Fade.InstantIn(textAnimation);
            }

            Fade.In(status, 0.3);
            if (textAnimation is not null)
            {
                Animate.Once(textAnimation, 0, 1.5);
            }

            Wait.Seconds(1.5);
            if (textAnimation is not null)
            {
                Fade.InstantOut(textAnimation);
                Animate.Instant(textAnimation, 1);
            }

            Action.BlockOn(Fade.Out(status, 1));

            if (key == "txtPlayerBonus")
            {
                Scale.Instant(status, 1 / (1.5 * fit));
                if (textAnimation is not null)
                {
                    Scale.Instant(textAnimation, 4.0 / 3.0);
                }
            }
            else
            {
                Scale.Instant(status, 1 / (2 * fit), 0);
            }
        }
        else if (player == 0)
        {
            if (statusText is not null)
            {
                SetText.Replace(statusText, key);
            }
        }
        else
        {
            string group = "Status" + player;
            Action.BlockOn(Scale.Thin(status, 100, 0.15, Action.StyleEaseOut, group));

            if (key == "txtNPCBusted")
            {
                if (statusText is not null)
                {
                    SetText.LocReplace(statusText, key, group);
                    Fade.InstantOut(statusText, group);
                }

                if (bustedX is not null)
                {
                    Fade.InstantIn(bustedX, group);
                }
            }
            else
            {
                if (bustedX is not null)
                {
                    Fade.InstantOut(bustedX, group);
                }

                if (statusText is not null)
                {
                    Fade.InstantIn(statusText, group);
                    SetText.LocWithVars(statusText, key, Vars("CHIPS", amount), group);
                }
            }

            Action.BlockOn(Scale.Thick(status, 100, 0.15, Action.StyleEaseIn, group));
        }

        VPlayers[player].StatusText = line;
    }

    public void UpdatePlayerInfo(GameLogic? game, int player, bool statusonly = false)
    {
        if (NotMyTable(game))
        {
            return;
        }

        if (statusonly)
        {
            UpdatePlayerStatusInfo(player);
            return;
        }

        UpdatePlayerChipInfo(player);
        UpdatePlayerStatusInfo(player);
    }

    public void UpdateAllPlayerChipInfo(GameLogic? game = null)
    {
        if (NotMyTable(game))
        {
            return;
        }

        for (var i = 0; i < 5; i++)
        {
            UpdatePlayerChipInfo(i);
        }
    }

    public void UpdateAllPlayerStatusInfo(GameLogic? game = null)
    {
        if (NotMyTable(game))
        {
            return;
        }

        for (var i = 0; i < 5; i++)
        {
            UpdatePlayerStatusInfo(i);
        }
    }

    public void UpdateAllPlayerInfo(GameLogic? game = null)
    {
        if (NotMyTable(game))
        {
            return;
        }

        UpdateAllPlayerChipInfo();
        UpdateAllPlayerStatusInfo();
    }

    public void SetForegroundText(GameLogic? game, string fieldname, string newtext)
    {
        if (NotMyTable(game))
        {
            return;
        }

        IPubClip? field = Clip("foregroundclip")?.GetChild(fieldname);
        if (field is null)
        {
            return;
        }

        if (newtext.Length == 0)
        {
            Fade.Out(field, 0.5);
            return;
        }

        Fade.In(field, 0.5);
        SetText.Replace(field, newtext);
    }

    public void PlayerEnters(GameLogic? game, int player)
    {
        if (NotMyTable(game) || ((Tutorial || NoSinglePlayerAIs) && player > 0))
        {
            return;
        }

        IPubClip? label = Clip("Name" + player)?.GetChild("Label");
        if (label is not null)
        {
            SetText.Replace(label, VPlayers[player].Name);
        }

        FadeSeat(player, fadeIn: true);
    }

    public void PlayerExits(GameLogic? game, int player)
    {
        if (NotMyTable(game))
        {
            return;
        }

        FadeSeat(player, fadeIn: false);
    }

    private void FadeSeat(int player, bool fadeIn)
    {
        foreach (string name in new[]
        {
            "Status" + player, "Name" + player, "ChipTotal" + player,
            "Stack" + player + "A", "Stack" + player + "B", "Stack" + player + "C",
        })
        {
            IPubClip? clip = Clip(name);
            if (clip is null)
            {
                continue;
            }

            if (fadeIn)
            {
                Fade.In(clip, 1);
            }
            else
            {
                Fade.Out(clip, 1);
            }
        }
    }

    public void UpdateCashoutMarkers(GameLogic? game, int row, int count, bool human = false)
    {
        if (NotMyTable(game))
        {
            return;
        }

        IPubClip? markers = Clip("Markers");
        if (markers is null)
        {
            return;
        }

        if (row > 1)
        {
            IPubClip? rowMarkers = markers.GetChild("Markers" + row);
            for (var i = 1; i <= count; i++)
            {
                IPubClip? marker = rowMarkers?.GetChild("marker" + count);
                if (marker is not null)
                {
                    Animate.Instant(marker, human ? 2 : 3);
                }
            }

            return;
        }

        for (var r = 2; r <= VDeck!.NumberOfRows; r++)
        {
            IPubClip? rowMarkers = markers.GetChild("Markers" + r);
            for (var i = 1; i <= 5; i++)
            {
                IPubClip? marker = rowMarkers?.GetChild("marker" + i);
                if (marker is not null && marker.CurrentFrame > 1)
                {
                    Animate.Instant(marker, 1);
                }
            }
        }
    }

    public void UpdateTextGlow(GameLogic? game, int row)
    {
    }

    public void SetCardFrame(int cardindex, int cardvalue, double? delay, object? group = null)
    {
        if (Cards.CardClips is null)
        {
            return;
        }

        Action switched = delay is not null
            ? SwitchCard.Delayed(Cards.CardClips, "Card" + cardindex, cardvalue, string.Empty, delay.Value, group)
            : SwitchCard.Instant(Cards.CardClips, "Card" + cardindex, cardvalue, string.Empty, group);

        Cards.Get("Card" + cardindex).CardValue = cardvalue;
        switched.AnimStyle = cardvalue is >= 0 and <= 9 ? Action.StyleEaseOut : Action.StyleEaseIn;

        if (cardvalue >= 0 && VGame!.PlayerCashOutRow < 100
            && VGame.CurrentRow > VGame.PlayerCashOutRow
            && (cardindex == 0 || cardindex >= DeckOfCards.RowIndex(VGame.PlayerCashOutRow + 1)))
        {
            IPubClip? card = Card(cardindex);
            if (card is not null)
            {
                Tint tint = Tint.Apply(card, RedTint, GreenTint, BlueTint, 0, group);
                if (delay is not null)
                {
                    Action.Delay(tint, delay.Value);
                }
            }
        }
    }

    public void SetCardAnimation(int cardindex, string anim, object? group = null)
    {
        if (Cards.CardClips is null)
        {
            return;
        }

        CardClipData data = Cards.Get("Card" + cardindex);
        SwitchCard.Instant(Cards.CardClips, "Card" + cardindex, data.CardValue, anim, group);

        if (VGame!.PlayerCashOutRow < 100 && VGame.CurrentRow > VGame.PlayerCashOutRow
            && (cardindex == 0 || cardindex >= DeckOfCards.RowIndex(VGame.PlayerCashOutRow + 1)))
        {
            IPubClip? card = Card(cardindex);
            if (card is not null)
            {
                Tint.Apply(card, RedTint, GreenTint, BlueTint, 0, group);
            }
        }

        if (data.CardValue == 0 && anim == "Idle")
        {
            Animate.OnceByName(Cards.CardClips, "Card" + cardindex, 0, 1.6);
        }
        else if (data.CardValue == 0 && anim == "Cheer")
        {
            Action.BlockOn(
                Animate.OnceByName(Cards.CardClips, "Card" + cardindex, 0, 0.7, cardindex));
            SetCardAnimation(cardindex, "Bonus", cardindex);
        }
        else if (data.CardValue == 0 && anim == "Saved")
        {
            Animate.OnceByName(Cards.CardClips, "Card" + cardindex, 0, 2.25);
        }
        else if (anim == "Paired")
        {
            Action.BlockOn(
                Animate.OnceByName(Cards.CardClips, "Card" + cardindex, 0, 0.5, cardindex));
            SetCardAnimation(cardindex, "Burning", cardindex);
        }
        else if (anim == "Saved")
        {
            Action.BlockOn(
                Animate.OnceByName(Cards.CardClips, "Card" + cardindex, 0, 0.5, cardindex));
        }
    }

    public void RemoveCardAnimation(int cardindex) => SetCardAnimation(cardindex, string.Empty);

    public void HighlightCard(int card)
    {
        int clip = CardToMovieClip(card);
        CardClipData data = Cards.Get("Card" + clip);
        if (data.Stacked)
        {
            return;
        }

        SetCardAnimation(clip, "Paired");
        data.Stacked = true;
        data.Bonus = false;
    }

    public void UnHighlightCard(int card)
    {
        int clip = CardToMovieClip(card);
        CardClipData data = Cards.Get("Card" + clip);
        if (!data.Stacked)
        {
            return;
        }

        SetCardAnimation(clip, "Saved");
        data.Stacked = false;
        data.Bonus = false;
    }

    public void HighlightBonusCard(int card)
    {
        if (card > VGame!.CurrentCard)
        {
            return;
        }

        int clip = CardToMovieClip(card);
        CardClipData data = Cards.Get("Card" + clip);
        if (data.Bonus)
        {
            return;
        }

        SetCardAnimation(clip, "Bonus", clip);

        if (card > VGame.CurrentCard - VGame.CurrentRow)
        {
            IPubClip? target = Card(clip);
            if (target is not null)
            {
                Action.BlockOn(Scale.Up(target, 1.15, 0.25, Action.StyleEaseIn, clip));
                Scale.Down(target, 1.15, 0.25, Action.StyleEaseOut, clip);
            }
        }

        data.Bonus = true;
        data.Stacked = false;
    }

    public void UnHighlightBonusCard(int card)
    {
        int clip = CardToMovieClip(card);
        CardClipData data = Cards.Get("Card" + clip);
        if (!data.Bonus)
        {
            return;
        }

        RemoveCardAnimation(clip);
        data.Bonus = false;
        data.Stacked = false;
    }

    public void HighlightBonusRow(int row)
    {
        int start = DeckOfCards.RowIndex(row);
        if (VDeck!.RowBonus(row) <= 1)
        {
            return;
        }

        if (row == VGame!.CurrentRow)
        {
            int last = CardToMovieClip(start + row - 1);
            if (!Cards.Get("Card" + last).Bonus)
            {
                PlaySound.Once(row switch
                {
                    2 => "W_bonusSetx2",
                    3 => "W_bonusSetx3",
                    4 => "W_bonusSetx4",
                    _ => "W_bonusSetxLots",
                });

                if (VGame.HandsPlayed == 1)
                {
                    AddTutorialCallback();
                    AddTutorialCallback();
                }
            }
        }

        for (int i = start + row - 1; i >= start; i--)
        {
            HighlightBonusCard(i);
        }

        for (int i = start - 1; i >= 0; i--)
        {
            UnHighlightBonusCard(i);
        }
    }

    public void UnHighlightAllBonusRows()
    {
        for (int i = VGame!.CurrentCard; i >= 0; i--)
        {
            UnHighlightBonusCard(i);
        }
    }

    public void HighlightJackpotCard(int card)
    {
        if (card > VGame!.CurrentCard)
        {
            return;
        }

        int clip = CardToMovieClip(card);
        CardClipData data = Cards.Get("Card" + clip);
        if (data.Jackpot)
        {
            return;
        }

        if (data.CardValue == 0)
        {
            SetCardAnimation(clip, "Cheer");
            PlaySound.Once("W_heroCardCheer");
        }
        else
        {
            SetCardAnimation(clip, "Bonus");
        }

        IPubClip? target = Card(clip);
        if (target is not null)
        {
            Action.BlockOn(Scale.Up(target, 1.15, 0.25, Action.StyleEaseIn, clip));
            Scale.Down(target, 1.15, 0.25, Action.StyleEaseOut, clip);
        }

        if (card % 4 == 0)
        {
            PlaySound.Once("K_showWinHilight");
        }

        Wait.Seconds(0.05);
        data.Jackpot = true;
        data.Bonus = false;
        data.Stacked = false;
    }

    public void UnHighlightJackpotCard(int card)
    {
        int clip = CardToMovieClip(card);
        CardClipData data = Cards.Get("Card" + clip);
        if (!data.Jackpot)
        {
            return;
        }

        RemoveCardAnimation(clip);
        data.Jackpot = false;
        data.Bonus = false;
        data.Stacked = false;
    }

    public void DeHighlightCards()
    {
        for (int i = VGame!.CurrentCard + 1; i >= 0; i--)
        {
            int card = i;
            if (i == VGame.CurrentCard + 1)
            {
                if (!VGame.CheatUsed)
                {
                    continue;
                }

                card = VDeck!.TotalCards - 1;
            }

            CardClipData data = Cards.Get("Card" + CardToMovieClip(card));
            if (data.Stacked)
            {
                UnHighlightCard(card);
            }

            if (data.Bonus)
            {
                UnHighlightBonusCard(card);
            }

            if (data.Jackpot)
            {
                UnHighlightJackpotCard(card);
            }
        }
    }

    public void HighlightAllBonusRows()
    {
        var total = 0;
        int bonus = VDeck!.TotalBonus(VGame!.CurrentRow);
        double stake = 1;
        if (VPlayers[0].SafetyStakes != 0)
        {
            stake = VPlayers[0].SafetyStakes / 15.0;
        }

        IPubClip? head = Clip("TotalBoxes")?.GetChild("Total" + VGame.CurrentRow);

        for (int row = VGame.CurrentRow; row > 1; row--)
        {
            int start = DeckOfCards.RowIndex(row);
            for (int i = start; i <= start + row - 1; i++)
            {
                HighlightJackpotCard(i);
            }

            total += VDeck.RowValue(row);

            IPubClip? box = Clip("TotalBoxes")?.GetChild("Total" + row);
            if (box is not null)
            {
                Fade.In(box, 0.2);
                Action.BlockOn(Scale.Up(box, 1.15, 0.25, Action.StyleEaseIn, -row));
                Scale.Down(box, 1.15, 0.25, Action.StyleEaseOut, -row);
            }

            IPubClip? headText = head?.GetChild("StatusText");
            if (headText is not null)
            {
                SetText.Replace(headText, total.ToString(CultureInfo.InvariantCulture));
            }

            IPubClip? pays = OfferText("PaysText");
            IPubClip? offer = OfferText("OfferText");
            IPubClip? middle = OfferText("TextMiddle");

            if (pays is not null)
            {
                if (bonus == 1)
                {
                    SetText.LocWithVars(pays, "txtTotalPays", Vars("TOTAL", total), "OfferGroup");
                }
                else
                {
                    SetText.LocWithVars(
                        pays, "txtBonusPays", Vars("TOTAL", total, "BONUS", bonus), "OfferGroup");
                }
            }

            if (offer is not null)
            {
                SetText.LocWithVars(
                    offer, "txtOfferAmount", Vars("CHIPS", total * bonus * stake), "OfferGroup");
            }

            if (middle is not null)
            {
                SetText.Replace(middle, string.Empty, "OfferGroup");
            }
        }

        IPubClip? animating = Clip("OfferAnimating")?.GetChild("OfferText");
        if (animating is not null)
        {
            SetText.LocWithVars(animating, "txtOfferAmount", Vars("CHIPS", total * bonus * stake));
        }
    }

    public void PlayRandomIdleAnimation()
    {
        if (!Tutorial)
        {
            for (int i = VGame!.CurrentCard; i > 0; i--)
            {
                if (VDeck!.Cards[i] != 0)
                {
                    continue;
                }

                int clip = CardToMovieClip(i);
                CardClipData data = Cards.Get("Card" + clip);
                if (!data.Jackpot && !data.Bonus && !data.Stacked && !data.Idle)
                {
                    SetCardAnimation(clip, "Idle");
                    data.Idle = true;
                    PlaySound.Once("W_heroCardIdle");
                    return;
                }
            }
        }

        ResetIdleFlags();
    }

    public void ResetIdleFlags()
    {
        for (int i = VGame!.CurrentCard; i > 0; i--)
        {
            int clip = CardToMovieClip(i);
            CardClipData data = Cards.Get("Card" + clip);
            if (data.Idle)
            {
                RemoveCardAnimation(clip);
                data.Idle = false;
            }
        }
    }

    public void AnimateTheBurnCard(GameLogic? game)
    {
        if (NotMyTable(game) || VGame is null || VDeck is null)
        {
            return;
        }

        IPubClip? card0 = Card(0);

        if (VGame.BurnCardUsed && card0 is not null)
        {
            Wait.Seconds(0.5);
            PlaySound.Once("W_gateCardTravel");
            MoveTo.Depth(card0, VDeck.TotalCards + (VDeck.BurnedCard * 2) + 1);
            MoveTo travel = MoveTo.Linear(
                card0, BoardXPosition[VDeck.BurnedCard], BoardYPosition[VDeck.BurnedCard],
                4 * CardSpeed, Action.StyleEaseIn);
            Rotate.SynchedClockwise(card0, 4, travel, Action.StyleEaseIn);
            SetCardFrame(0, VDeck.Cards[VDeck.BurnedCard], travel.TimeLeft() / 3000);
            Scale.Thick(card0, 1.25, travel.TimeLeft() / 3000, Action.StyleEaseOut, 0);
            Action.BlockOn(Scale.Short(card0, 4, travel.TimeLeft() / 3000, Action.StyleEaseOut, 0));
            Scale.Thin(card0, 1.25, travel.TimeLeft() / 3000, Action.StyleEaseIn, 0);
            Scale.Tall(card0, 4, travel.TimeLeft() / 3000, Action.StyleEaseIn, 0);
            Wait.UntilFinished();

            IPubClip? landed = Card(VDeck.BurnedCard);
            if (landed is not null)
            {
                Rotate.TimedCounter(landed, -4, 0.3);
            }
        }

        if (!VDeck.RowIsBusted(VGame.CurrentRow))
        {
            if (VDeck.Cards[VDeck.BurnedCard] == 0)
            {
                PlayKnightSaviorSequence(
                    VGame.CurrentCard + 1 - VGame.CurrentRow, VGame.CurrentCard);
            }
            else
            {
                for (int i = VGame.CurrentCard; i >= 0; i--)
                {
                    UnHighlightCard(i);
                }

                PlaySound.Once("W_gateCardSave");
            }

            Wait.UntilFinished();
            HighlightBonusRow(VDeck.RowBonus(VGame.CurrentRow) > 1
                ? VGame.CurrentRow
                : VGame.CurrentRow - 1);
            AnimateOfferInfo(VDeck.RowValue(VGame.CurrentRow));
        }
        else
        {
            UnHighlightAllBonusRows();
            if (VDeck.Cards[VDeck.BurnedCard] != VDeck.Cards[0])
            {
                UnHighlightCard(0);
            }

            if (VDeck.BurnedCard != DeckOfCards.RowIndex(VGame.CurrentRow))
            {
                int left = VDeck.BurnedCard - VGame.CurrentRow;
                bool pairs = VDeck.Cards[VDeck.BurnedCard] == VDeck.Cards[left];
                if (pairs && !Cards.Get("Card" + left).Stacked)
                {
                    HighlightCard(left);
                }

                if (!pairs && Cards.Get("Card" + left).Stacked
                    && VDeck.Cards[VDeck.BurnedCard - 1] != VDeck.Cards[left])
                {
                    UnHighlightCard(left);
                }

                if (pairs)
                {
                    HighlightCard(VDeck.BurnedCard);
                }
            }

            if (VDeck.BurnedCard != DeckOfCards.RowIndex(VGame.CurrentRow + 1) - 1)
            {
                int right = VDeck.BurnedCard - VGame.CurrentRow + 1;
                bool pairs = VDeck.Cards[VDeck.BurnedCard] == VDeck.Cards[right];
                if (pairs && !Cards.Get("Card" + right).Stacked)
                {
                    HighlightCard(right);
                }

                if (!pairs && Cards.Get("Card" + right).Stacked
                    && VDeck.Cards[VDeck.BurnedCard + 1] != VDeck.Cards[right])
                {
                    UnHighlightCard(right);
                }

                if (pairs && !Cards.Get("Card" + CardToMovieClip(VDeck.BurnedCard)).Stacked)
                {
                    HighlightCard(VDeck.BurnedCard);
                }
            }
        }

        EndAnimateOfferInfo();
    }

    public void DecrementCheat()
    {
    }

    public void AnimateTheCheatCard(GameLogic? game)
    {
    }

    public void DealCards(GameLogic? game, int startcard, int? endcard = null)
    {
        if (NotMyTable(game) || VGame is null || VDeck is null)
        {
            return;
        }

        int last = endcard ?? startcard;
        VGame.CashOutCount = 0;
        Action.RunOnce(this, ResetDeckDepths);
        Wait.Seconds(0);

        var sawHero = false;
        var sawPair = false;
        var running = 0;

        if (startcard != last)
        {
            StartAnimateOfferInfo();
        }

        for (int i = startcard; i <= last; i++)
        {
            if (VPlayers[0].Out)
            {
                Action.RunOnce(this, SetPlayerSpeedFactor);
            }

            int value = i != 0 ? VDeck.Cards[i] : -1;
            bool tinted = value >= 0 && VGame.PlayerCashOutRow < 100
                && VGame.CurrentRow > VGame.PlayerCashOutRow
                && (i == 0 || i >= DeckOfCards.RowIndex(VGame.PlayerCashOutRow + 1));

            IPubClip? clip = Card(i);
            if (clip is not null)
            {
                DealCard.ToBoard(
                    clip, "Card" + i, value,
                    BoardXPosition[i] + (Chance!.NextDouble() * 6) - 3,
                    BoardYPosition[i] + (Chance.NextDouble() * 6) - 3,
                    (Chance.NextDouble() * 6) - 3,
                    VDeck.TotalCards + (i * 2), 10 * CardSpeed, i != 0,
                    tinted ? RedTint : 0, tinted ? GreenTint : 0, tinted ? BlueTint : 0);
            }

            Cards.Get("Card" + i).CardValue = value;

            if (VDeck.Cards[i] == 0)
            {
                sawHero = true;
            }

            if (running >= 0)
            {
                running += VDeck.Cards[i];
            }

            if (VDeck.IsCardStacked(i))
            {
                running = -1;
                sawPair = true;
                PlaySound.Once("W_pairedCard");
                HighlightCard(i);

                if (i != startcard && VDeck.Cards[i] == VDeck.Cards[i - VGame.CurrentRow])
                {
                    HighlightCard(i - VGame.CurrentRow);
                }

                if (i != last && VDeck.Cards[i] == VDeck.Cards[i - VGame.CurrentRow + 1])
                {
                    HighlightCard(i - VGame.CurrentRow + 1);
                }

                Wait.Seconds(0.1);
                Wait.UntilFinished();
            }
            else
            {
                Wait.Seconds(0.1);
            }

            if (startcard + 1 == last && i == startcard)
            {
                IPubClip? offer = Clip("OfferInfo");
                if (offer is not null)
                {
                    Fade.In(offer, 0.5);
                }
            }

            if (startcard != last)
            {
                AnimateOfferInfo(running);
            }
        }

        if (sawPair)
        {
            if (sawHero)
            {
                if (VGame.HandsPlayed == 0)
                {
                    AddTutorialCallback();
                }

                PlayKnightSaviorSequence(startcard, last);

                if (VGame.HandsPlayed == 0)
                {
                    AddTutorialCallback();
                }

                AnimateOfferInfo(VDeck.RowValue(VGame.CurrentRow));
            }
            else if (VDeck.RowBonus(VGame.CurrentRow) > 1)
            {
                for (int i = last; i >= 0; i--)
                {
                    if (Cards.Get("Card" + CardToMovieClip(i)).Stacked)
                    {
                        UnHighlightCard(i);
                    }
                }

                AnimateOfferInfo(VDeck.RowValue(VGame.CurrentRow));
            }

            HighlightBonusRow(VGame.CurrentRow - 1);
        }

        HighlightBonusRow(VGame.CurrentRow);

        if (startcard != last
            && (!sawPair || sawHero || VDeck.RowBonus(VGame.CurrentRow) > 1))
        {
            EndAnimateOfferInfo();
        }
    }

    public void SweepCards(GameLogic? game = null)
    {
        if (NotMyTable(game) || VGame is null || VDeck is null)
        {
            return;
        }

        Action.RunOnce(this, ClearPlayerSpeedFactor);
        PlaySound.Once("W_clearCards");
        Action.RunOnce(this, ResetCardDepths);

        for (int i = VGame.CurrentCard + 1; i >= 0; i--)
        {
            int card = i;
            if (i == VGame.CurrentCard + 1)
            {
                if (!VGame.CheatUsed)
                {
                    continue;
                }

                card = VDeck.TotalCards - 1;
            }

            IPubClip? clip = Card(card);
            if (clip is null)
            {
                continue;
            }

            SweepCard.ToDeck(
                clip, "Card" + card,
                DeckXPosition + (Chance!.NextDouble() * DeckPositionVariation),
                DeckYPosition + (Chance.NextDouble() * DeckPositionVariation) + DeckOffset(card),
                DeckAngle + (Chance.NextDouble() * DeckAngleVariation),
                (15 + (Chance.NextDouble() * 2)) * CardSpeed, "Sweep");
        }

        Wait.UntilFinished("Sweep");
        PlaySound.Once("W_shuffle");
    }

    public void SetButtonsAfterSweep(GameLogic? game)
    {
        if (NotMyTable(game))
        {
            return;
        }

        Wait.Seconds(0.5);
        SetAllButtons(VGame, "txtDealHand", string.Empty, YButtonText);
        SetWinAmount(VGame, 0);
    }

    public void BustTheRow(GameLogic? game)
    {
        if (NotMyTable(game))
        {
            return;
        }

        if (VPlayers[0].In)
        {
            PlaySound.Once("W_busted");
        }

        UpdateBonusState();
        UpdateAllPlayerStatusInfo();
        SetAllButtons(VGame, "txtPlayAgain", string.Empty, YButtonText);

        if (VPlayers[0].In)
        {
            SetWinAmount(VGame, 0);
        }
    }

    public void HitTheJackpot(GameLogic? game)
    {
        if (NotMyTable(game))
        {
            return;
        }

        Action.RunOnce(this, ClearPlayerSpeedFactor);
        Wait.UntilFinished();
        Wait.Seconds(1);
        HighlightAllBonusRows();
        PlaySound.Once("W_jackpot");

        if (!VPlayers[0].Out)
        {
            PlaySound.Once("applause05_jackpot");
        }
    }

    public void PlayKnightSaviorSequence(int startcard, int endcard)
    {
        double lastHero = -1000;
        double nextHero = double.MaxValue;
        var heroes = 0;

        for (int i = startcard; i <= endcard; i++)
        {
            if (VDeck!.Cards[i] == 0)
            {
                lastHero = i;
                SetCardAnimation(CardToMovieClip(i), "Saved");
                PlaySound.Delayed("W_heroCardSave", heroes * 0.05);
                heroes++;
                continue;
            }

            if (i > nextHero || nextHero == double.MaxValue)
            {
                for (int j = i + 1; j <= endcard; j++)
                {
                    if (VDeck.Cards[j] == 0)
                    {
                        nextHero = j;
                        break;
                    }
                }

                if (i > nextHero || nextHero == double.MaxValue)
                {
                    nextHero = 1000;
                }
            }

            double distance = Math.Min(i - lastHero, nextHero - i);

            IPubClip? clip = Card(CardToMovieClip(i));
            if (clip is null)
            {
                continue;
            }

            Scale up = Scale.Up(clip, 1.25, 0.15, Action.StyleEaseIn, i);
            up.Time = -((distance * 50 * (1 + ((distance - 1) / 6))) + 1300);
            Action.BlockOn(up);
            Scale.Down(clip, 1.25, 0.15, Action.StyleEaseOut, i);
        }

        Wait.Seconds(1.3);

        var saved = 0;
        for (int i = endcard; i >= 0; i--)
        {
            if (!Cards.Get("Card" + CardToMovieClip(i)).Stacked)
            {
                continue;
            }

            UnHighlightCard(i);
            if (i >= startcard && saved == 0)
            {
                PlaySound.Delayed("W_savedCard", 0);
            }

            saved++;
        }
    }

    public void SetPushOnAndCashOutButtons(GameLogic? game)
    {
        if (NotMyTable(game) || VGame is null || VDeck is null)
        {
            return;
        }

        SetButtonText(
            "A", VGame.CurrentRow < VDeck.NumberOfRows ? "txtDealRow" : "txtPlayAgain");

        if (VGame.CurrentRow < VDeck.NumberOfRows)
        {
            SetWinAmount(VGame, 0);
            SetButtonText("B", "txtTakeWin");
        }
        else
        {
            SetWinAmount(VGame, VGame.CashOutValue * (double)VPlayers[0].SafetyStakes / 15);
            SetButtonText("B", string.Empty);
        }

        SetButtonText("Y", YButtonText);
    }

    public void SetBetAmount(GameLogic? game, int amount)
    {
        if (NotMyTable(game))
        {
            return;
        }

        IPubClip? field = Clip("BetInfo")?.GetChild("BetAmount");
        if (field is not null)
        {
            SetText.LocWithVars(field, "txtInfoAmount", Vars("CHIPS", amount));
        }
    }

    public void SetWinAmount(GameLogic? game, double amount)
    {
        if (NotMyTable(game))
        {
            return;
        }

        IPubClip? field = Clip("BetInfo")?.GetChild("WinAmount");
        if (field is not null)
        {
            SetText.LocWithVars(field, "txtInfoAmount", Vars("CHIPS", amount));
        }
    }

    public void SetAllButtons(
        GameLogic? game, string? buttonA, string? buttonB, string? buttonY, string? buttonX = null)
    {
        if (NotMyTable(game))
        {
            return;
        }

        if (buttonA is not null)
        {
            SetButtonText("A", buttonA);
        }

        if (buttonB is not null)
        {
            SetButtonText("B", buttonB);
        }

        if (buttonX is not null)
        {
            SetButtonText("X", buttonX);
        }

        if (buttonY is not null)
        {
            SetButtonText("Y", buttonY);
        }
    }

    public void AddTutorialCallback(bool dontblock = false)
    {
    }

    public void InsertTutorialCallback(bool dontblock = false)
    {
        if (!Tutorial)
        {
            return;
        }

        IPubClip? foreground = Clip("foregroundclip");
        if (foreground is null)
        {
            return;
        }

        if (!TutorialDialog)
        {
            TutorialPauseUntilA = true;
            TutorialStep = 0;
            IPubClip? card = foreground.AttachMovie("TutorialClip", "Tutorial", 1);
            if (card is not null)
            {
                card.X = 700;
                card.Y = 425;
                card.Alpha = 0;
                card.GotoAndStop(2);
            }

            TutorialDialog = true;
        }

        IPubClip? dialog = foreground.GetChild("Tutorial");
        if (dialog is null)
        {
            return;
        }

        Fade.In(dialog, 0.5);

        if (dontblock)
        {
            Action.RunUntilTrue(this, DisplayTutorialDialog);
            return;
        }

        Action.RunOnce(this, TutorialInputBlockOff);
        Action.BlockOn(Action.RunUntilTrue(this, DisplayTutorialDialog));
        Action.RunOnce(this, TutorialInputBlockOn);
        Action.BlockOn(Fade.Out(dialog, 0.5));
    }

    public void TutorialInputBlockOn() => TutorialInputBlock = true;

    public void TutorialInputBlockOff() => TutorialInputBlock = false;

    public void ClearShownDeck()
    {
        InsertTutorialCallback();
        ShowingDeck = true;
        Wait.UntilFinished();

        if (!Tutorial && !TournamentFinalTable)
        {
            Wait.Seconds(2);
        }

        InsertTutorialCallback();
        Wait.Seconds(0);
        PlaySound.Once("W_clearCards");
        Action.RunOnce(this, ResetCardDepths);

        for (var i = 0; i < VDeck!.TotalCards; i++)
        {
            IPubClip? clip = Card(i);
            if (clip is null)
            {
                continue;
            }

            MoveTo travel = MoveTo.Linear(
                clip,
                DeckXPosition + (Chance!.NextDouble() * DeckPositionVariation),
                DeckYPosition + (Chance.NextDouble() * DeckPositionVariation) + DeckOffset(i),
                (15 + (Chance.NextDouble() * 2)) * CardSpeed, Action.StyleEaseOut);
            Rotate.Synched(
                clip, DeckAngle + (Chance.NextDouble() * DeckAngleVariation), travel,
                Action.StyleEaseOut);
            SetCardFrame(i, -1, travel.TimeLeft() / 2000);
        }

        Wait.UntilFinished();
        Action.RunOnce(this, DoneShowingDeck);
    }

    public void DoneShowingDeck()
    {
        for (int i = CardsShown; i < VDeck!.TotalCards; i++)
        {
            Card(i).RemoveMovieClip();
        }

        ShowingDeck = false;
        InsertTutorialCallback();
        InsertTutorialCallback();

        if (!FinalTableAnims)
        {
            return;
        }

        IPubClip? message = Clip("textmessage");
        if (message is null)
        {
            return;
        }

        IPubClip? body = message.GetChild("body");
        if (body is not null)
        {
            SetText.LocWithVars(
                body, "txtMadeFinalTable", Vars("CHIPS", TournamentStartingChips * 2));
        }

        IPubClip? label = message.GetChild("button1")?.GetChild("label");
        if (label is not null)
        {
            SetText.LocReplace(label, "txtContinue");
        }

        message.GetChild("button1")?.GetChild("button")?.GotoAndStop("x");
        Fade.In(message, 0.5);
        PlaySound.Once("applause04_large");
    }

    public void FinishGameViewInit()
    {
        if (!Tournament || !TournamentFinalTable)
        {
            IPubClip? mask = Clip("BlackMask");
            if (mask is not null)
            {
                Fade.Out(mask, 0.5);
            }
        }

        Action.RunOnce(VGame, VGame!.SetReadyToTrue);
        PlaySound.Once("W_introFlourish");

        if (!Tournament || !TournamentFinalTable)
        {
            Action.RunOnce(this, ClearShownDeck);
        }

        Action.RunOnce(this, PostGameViewInit);
    }

    public void PostGameViewInit()
    {
        UpdateAllPlayerInfo();
        SetAllButtons(VGame, "txtDealHand", string.Empty, YButtonText);

        for (var i = 0; i < 5; i++)
        {
            if ((Tutorial || NoSinglePlayerAIs) && i > 0)
            {
                continue;
            }

            IPubClip? label = Clip("Name" + i)?.GetChild("Label");
            if (label is not null)
            {
                SetText.Replace(label, VPlayers[i].Name);
            }

            FadeSeat(i, fadeIn: true);
        }

        IPubClip? bet = Clip("BetInfo");
        if (bet is not null)
        {
            Fade.In(bet, 1);
        }

        if (MaxBet > 15)
        {
            if (!Tutorial)
            {
                IPubClip? ls = Clip("LSbutton");
                IPubClip? lsText = Clip("LSButtonText");
                if (ls is not null)
                {
                    Fade.In(ls, 1);
                }

                if (lsText is not null)
                {
                    Fade.In(lsText, 1);
                }
            }

            ShowLSButton = true;
        }
    }

    public void PlayAIDecisionSound(GameLogic? game, int position)
    {
    }
}
