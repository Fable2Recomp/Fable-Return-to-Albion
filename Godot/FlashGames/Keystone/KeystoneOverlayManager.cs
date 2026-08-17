using System.Collections.Generic;

using Refii.FlashGames.Framework;

namespace Refii.FlashGames.Keystone;

public sealed class KeystoneOverlayManager : OverlayManager
{
    private readonly Keystone _keystone;

    public KeystoneOverlayManager(Keystone keystone, IPubClip root)
        : base(keystone, root) => _keystone = keystone;

    protected override int GetPlays() => _keystone.TotalRolls;

    protected override void InitQuickTips(IPubClip clip)
    {
        Localize(
            clip.GetChild("body"),
            _keystone.Bloodstone ? "txtOddsInfoBloodstone" : "txtOddsInfo",
            null);

        for (int i = 0; i < 8; i++)
        {
            Localize(clip.GetChild("value" + i), "txtOddsChip" + i, null);
        }

        string payoutKey = _keystone.Bloodstone ? "txtPayoutBloodstone" : "txtPayout";
        for (int space = KeystoneSpace.FirstBetSpace; space <= KeystoneSpace.LastBetSpace; space++)
        {
            int payout = _keystone.GetPayoutRate(space);
            int bet = _keystone.GetPayoutBet(space);
            Localize(
                clip.GetChild(KeystoneRules.BetIdToString(space) + "odds"),
                payoutKey,
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

    protected override void InitCashGameOverlay(IPubClip clip) =>
        Localize(
            clip.GetChild("title"),
            _keystone.Bloodstone
                ? "txtCashGameBloodstoneTitle"
                : _keystone.AllowJackpot
                    ? "txtCashGameJackpotTitle"
                    : "txtCashGameTitle",
            null);

    protected override void UpdateTournamentOverlay(IPubClip clip) =>
        Localize(
            clip.GetChild("subtitle"),
            "txtTournamentGamesRolls",
            new Dictionary<string, string>
            {
                ["game"] = _keystone.CurrentGame.ToString(),
                ["total"] = _keystone.TournamentRounds.ToString(),
                ["roll"] = _keystone.CurrentRoll.ToString(),
            });

    protected override void PulseBankButton() => _keystone.PulseBankButton();

    protected override void PulseStandingsButton() => _keystone.PulseStandingsButton();

    protected override void PulseQuickTipsButton() => _keystone.PulseQuickTipsButton();
}
