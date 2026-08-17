using System;
using System.Collections.Generic;

using Refii.FlashGames.Framework;

namespace Refii.FlashGames.MageDice;

/// <summary>
/// The overlay cards Mage Dice answers for itself, over the shared <see cref="OverlayManager"/>.
/// </summary>
/// <remarks>
/// A 1:1 port of <c>MageDiceOverlayManager.as</c>. Where Keystone's subclass prints a payout table
/// over board spaces, this one builds a paytable over reel symbols — same seam, entirely different
/// content, which is the reason the seam exists.
/// </remarks>
public sealed class MageDiceOverlayManager : OverlayManager
{
    private readonly MageDice _mageDice;

    private int _lastVariant = -1;
    private int _lastCost = -1;

    public MageDiceOverlayManager(MageDice mageDice, IPubClip root)
        : base(mageDice, root) => _mageDice = mageDice;

    // GetChipsInPlay IS NOT OVERRIDDEN, because the base does not declare it — see the note in
    // OverlayManager. Mage Dice's version returns the player's chips, which is exactly what the
    // AS2 base already returns, so nothing is lost by its absence: the override existed only to
    // restate the default.

    /// <summary>Mage Dice counts a play as a spin.</summary>
    protected override int GetPlays() => _mageDice.TotalSpins;

    /// <summary>
    /// Builds the odds card for this machine: its symbols, and what runs of them pay.
    /// </summary>
    /// <remarks>
    /// THE CARD HAS A FRAME PER LAYOUT, chosen from the reel count: three or four reels take frame
    /// 1 or 2, six takes frame 3 — except that a six-reel table where two in a row already pay
    /// needs the extra column, so it takes frame 4 instead.
    ///
    /// ONLY PAYING SYMBOLS GET A SLOT. Bonus reel, multiplier and free-spin symbols are pulled out
    /// of the run and shown once in the "special" position with an explanatory line, because they
    /// do not pay by run length like the rest.
    /// </remarks>
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

        // ALWAYS HIDDEN, in both branches of the shipped code — the symbol is swapped in but the
        // slot is never shown. Kept because removing it would change what the card looks like if
        // anything ever un-hides it.
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

    /// <summary>
    /// Refreshes the paytable when the stake changes.
    /// </summary>
    /// <remarks>
    /// THE CARD SHOWS CHIPS, NOT ODDS, so it is rebuilt whenever the bet moves — a payout of 10 at
    /// a stake of 5 is 50, and the player reads the number they would win rather than a ratio.
    /// </remarks>
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

    /// <summary>
    /// Names the pub this machine sits in.
    /// </summary>
    /// <remarks>
    /// THE VARIATION NAME IS TRIMMED DOWN TO THE VENUE: the 41 variations are things like
    /// <c>CowAndCorset1</c>, <c>SBTowerOfRiches4</c> and <c>JohnTheMiller</c>, and the localisation
    /// file carries nine keys, one per venue — so a trailing tier digit and a leading <c>SB</c> are
    /// stripped to get from one to the other.
    ///
    /// THE DIGIT TEST IS A TEST, though the shipped source does not read like one. It writes
    /// <c>if (Number(last) != NaN)</c>, which in ActionScript is TRUE for every value including NaN
    /// itself, so it would strip the last character unconditionally. The localisation file proves
    /// that is not the intent: three of the nine venues — <c>ConquestOfTheSeas</c>,
    /// <c>JohnTheMiller</c> and <c>Tutorial</c> — end in a letter, and stripping it would ask for
    /// <c>txtVariantSpinnerBoxConquestOfTheSea</c>, which does not exist. Testing for a digit
    /// resolves all nine. This is the same always-true NaN comparison that appears in
    /// <c>Fade.Run</c> and <c>Animate.Run</c>; see the note at the top of <c>Actions.cs</c>.
    /// </remarks>
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
