using System.Collections.Generic;
using System.Globalization;

using Refii.FlashGames.Framework;

namespace Refii.FlashGames.Keystone;

public sealed class KeystoneOverlayManager : OverlayManager
{
    private readonly Keystone _keystone;

    public KeystoneOverlayManager(Keystone keystone, IPubClip root)
        : base(keystone, root) => _keystone = keystone;

    protected override int GetPlays() => _keystone.TotalRolls;

    /// <summary><c>GetChipsInPlay</c> (0x000499): the game's own count for the human.</summary>
    public override int GetChipsInPlay() => _keystone.GetChipsInPlay(0);

    /// <summary>
    /// <c>GetChipsInPlayButNotLockedIn</c> (0x0004b9-0x000523): Bloodstone owes its early-exit
    /// payment; any other table hands back stakes whose points are not locked in.
    /// </summary>
    public override int GetChipsInPlayButNotLockedIn() =>
        _keystone.Bloodstone
            ? -_keystone.GetEarlyExitPayment()
            : _keystone.GetChipsNotLockedIn();

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
                    ["normalized10"] =
                        ((payout + bet) * 10.0 / bet).ToString(CultureInfo.InvariantCulture),
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
