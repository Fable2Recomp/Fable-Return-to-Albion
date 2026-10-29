using System.Collections.Generic;

using Refii.Titles.Fable2.PubGames.Framework;

namespace Refii.Titles.Fable2.PubGames.WizardsTower;

public sealed class WizardsTowerOverlayManager : OverlayManager
{
    private readonly WizardsTower _wizardsTower;

    public WizardsTowerOverlayManager(WizardsTower wizardsTower, IPubClip root)
        : base(wizardsTower, root) => _wizardsTower = wizardsTower;

    protected override int GetPlays() =>
        _wizardsTower.Games.Count > 0 ? _wizardsTower.Games[0].HandsPlayed : 0;

    protected override void PulseBankButton()
    {
        IPubClip? clip = _wizardsTower.Clip("Ybutton");
        if (clip is not null)
        {
            PulseClip(clip, 0);
        }
    }

    protected override void PulseStandingsButton()
    {
        IPubClip? clip = _wizardsTower.Clip("RTbutton");
        if (clip is not null)
        {
            PulseClip(clip, 0);
        }
    }

    protected override void PulseQuickTipsButton()
    {
        IPubClip? clip = _wizardsTower.Clip("LTbutton");
        if (clip is not null)
        {
            PulseClip(clip, 0);
        }
    }

    protected override void InitQuickTips(IPubClip clip)
    {
        foreach ((string field, string id) in new[]
        {
            ("GateCard", "txtLTGateCard"), ("GateCardText", "txtLTGateCardText"),
            ("Pair", "txtLTPair"), ("PairText", "txtLTPairText"),
            ("Hero", "txtLTHero"), ("HeroText", "txtLTHeroText"),
            ("Offer", "txtLTOffer"), ("OfferText", "txtLTOfferText"),
            ("Set", "txtLTSet"), ("SetText", "txtLTSetText"),
            ("Jackpot", "txtLTJackpot"), ("JackpotText", "txtLTJackpotText"),
        })
        {
            IPubClip? target = clip.GetChild(field);
            if (target is not null)
            {
                Game.Localizer.Localize(target, id, null);
            }
        }
    }

    protected override void UpdateQuickTips(IPubClip clip)
    {
    }

    protected override void InitCashGameOverlay(IPubClip clip)
    {
        IPubClip? title = clip.GetChild("title");
        if (title is not null)
        {
            Game.Localizer.Localize(title, "txtDeckType" + _wizardsTower.NumberOfCards + "Each", null);
        }
    }

    protected override void UpdateCashGameOverlay(IPubClip clip)
    {
    }

    protected override void InitTournamentOverlay(IPubClip clip)
    {
    }

    protected override void UpdateTournamentOverlay(IPubClip clip)
    {
        IPubClip? subtitle = clip.GetChild("subtitle");
        if (subtitle is null)
        {
            _wizardsTower.UpdateStandings();
            return;
        }

        if (_wizardsTower.TournamentOver)
        {
            Player? human = Game.PlayerInfo.GetPlayer(0);
            Game.Localizer.Localize(
                subtitle,
                _wizardsTower.TournamentFinalTable && human is not null
                    ? "txtTournamentPlaced" + human.Rank
                    : "txtDidNotMakeFinalTable",
                null);
        }
        else
        {
            string table = _wizardsTower.TournamentFinalTable ? "FinalTable" : "FirstTable";
            int hands = _wizardsTower.VGame?.HandsPlayed ?? 0;
            var variables = new Dictionary<string, string>
            {
                ["HANDS"] = hands.ToString(),
                ["MAXHANDS"] = Game.TournamentRounds.ToString(),
            };

            Game.Localizer.Localize(
                subtitle,
                (hands == 1 ? "txtOneHandPlayed" : "txtMultipleHandsPlayed") + table,
                variables);
        }

        _wizardsTower.UpdateStandings();
    }
}
