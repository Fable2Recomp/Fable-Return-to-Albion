using System;
using System.Collections.Generic;

using Refii.Titles.Fable2.PubGames.Framework;

namespace Refii.Titles.Fable2.PubGames.MageDice;

public sealed class MageDiceOverlayManager : OverlayManager
{
    private readonly MageDice _mageDice;

    private int _lastVariant = -1;
    private int _lastCost = -1;

    public MageDiceOverlayManager(MageDice mageDice, IPubClip root)
        : base(mageDice, root) => _mageDice = mageDice;

    protected override int GetPlays() => _mageDice.TotalSpins;

    protected override void InitQuickTips(IPubClip clip)
    {
        _lastVariant = _mageDice.ActiveVariantIndex;
        _lastCost = -1;

        int reels = _mageDice.Variant.GameType;
        int layout = reels / 2;
        if (layout == 3 && !_mageDice.Variant.ChainPaysOnTwo)
        {
            layout++;
        }

        clip.GotoAndStop(layout);

        List<string> images = _mageDice.Variant.ReelImages;
        List<int> types = _mageDice.Variant.Types;

        var body = "txtOddsBodyNormal";
        var slot = 0;
        var special = -1;

        for (var token = 0; token < images.Count; token++)
        {
            var paying = false;
            switch (types[token])
            {
                case Token.TypeBonusReel:
                    special = token;
                    body = "txtOddsBodyBonusReel";
                    break;
                case Token.TypeBonusMult:
                    special = token;
                    body = "txtOddsBodyBonusMultiplier";
                    break;
                case Token.TypeFreeSpin:
                    special = token;
                    body = "txtOddsBodyBonusSpin";
                    break;
                case Token.TypeChain:
                case Token.TypeBlank:
                    paying = true;
                    break;
            }

            if (!paying)
            {
                continue;
            }

            IPubClip? placeholder = clip.GetChild("token" + slot);
            if (placeholder is not null)
            {
                MageDice.ReplaceClip(clip, placeholder, images[token]);
            }

            slot++;
        }

        LocalizeChild(clip, "oddsText", body);
        LocalizeChild(clip, "oddsText2", "txtOddsBodyGeneral");

        if (clip.GetChild("title") is { } title)
        {
            SetVariantTitle(title);
        }

        if (clip.GetChild("special") is { } specialClip)
        {
            specialClip.Visible = false;
            if (special >= 0)
            {
                MageDice.ReplaceClip(clip, specialClip, images[special]);
            }
        }

        if (clip.GetChild("freeSpinLegend") is not { } legend)
        {
            return;
        }

        if (!_mageDice.Variant.HasBonusSpin)
        {
            legend.Visible = false;
            return;
        }

        for (var run = 2; run <= reels; run++)
        {
            int pays = _mageDice.Variant.TokenPays(special, run);
            IPubClip? entry = legend.GetChild("free" + run);
            if (entry is not null)
            {
                Localizer.Localize(
                    entry, pays <= 0 ? "txtOddsPayValueNone" : "txtOddsPayValue",
                    new Dictionary<string, string> { ["value"] = pays.ToString() });
            }
        }
    }

    protected override void UpdateQuickTips(IPubClip clip)
    {
        if (_lastVariant != _mageDice.ActiveVariantIndex)
        {
            InitQuickTips(clip);
        }

        int cost = _mageDice.GetBoard(0).CostPerSpin();
        if (_lastCost == cost)
        {
            return;
        }

        _lastCost = cost;
        int reels = _mageDice.Variant.GameType;
        List<int> types = _mageDice.Variant.Types;

        var column = 0;
        for (var token = 0; token < types.Count; token++)
        {
            if (types[token] != Token.TypeChain && types[token] != Token.TypeBlank)
            {
                continue;
            }

            for (var run = 2; run <= reels; run++)
            {
                int pays = _mageDice.Variant.TokenPays(token, run) * cost;
                IPubClip? cell = clip.GetChild("pay" + run + column);
                if (cell is not null)
                {
                    Localizer.Localize(
                        cell, pays <= 0 ? "txtOddsPayValueNone" : "txtOddsPayValue",
                        new Dictionary<string, string> { ["value"] = pays.ToString() });
                }
            }

            column++;
        }
    }

    protected override void InitCashGameOverlay(IPubClip clip)
    {
        if (clip.GetChild("title") is { } title)
        {
            SetVariantTitle(title);
        }
    }

    protected override void UpdateTournamentOverlay(IPubClip clip)
    {
        IPubClip? subtitle = clip.GetChild("subtitle");
        if (subtitle is not null)
        {
            Localizer.Localize(subtitle, "txtTournamentGames", new Dictionary<string, string>
            {
                ["game"] = _mageDice.TotalSpins.ToString(),
                ["total"] = Game.TournamentRounds.ToString(),
            });
        }
    }

    protected override void PulseBankButton() => _mageDice.PulseBankButton();

    protected override void PulseStandingsButton() => _mageDice.PulseStandingsButton();

    protected override void PulseQuickTipsButton() => _mageDice.PulseQuickTipsButton();

    private void SetVariantTitle(IPubClip t)
    {
        if (Game.Tournament)
        {
            Localizer.Localize(t, "txtTournamentTitle", null);
            return;
        }

        string name = Game.VariantName;
        if (name.Length > 0 && char.IsDigit(name[^1]))
        {
            name = name[..^1];
        }

        if (name.StartsWith("SB", StringComparison.Ordinal))
        {
            name = name[2..];
        }

        Localizer.Localize(t, "txtVariantSpinnerBox" + name, null);
    }

    private void LocalizeChild(IPubClip clip, string child, string key)
    {
        IPubClip? target = clip.GetChild(child);
        if (target is not null)
        {
            Localizer.Localize(target, key, null);
        }
    }

    private Localizer Localizer => Game.Localizer;
}
