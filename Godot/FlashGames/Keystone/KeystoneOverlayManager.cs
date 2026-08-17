using System.Collections.Generic;

using Refii.FlashGames.Framework;

namespace Refii.FlashGames.Keystone;

/// <summary>
/// The overlay cards Keystone answers for itself, over the shared <see cref="OverlayManager"/>.
/// </summary>
/// <remarks>
/// A 1:1 port of <c>KeystoneOverlayManager.as</c>, which is 102 lines over a base of 2,695. The
/// split is the shipped code's: everything about banking, help, quitting and the star rating is
/// the same for every pub game and lives in the base; the odds card, the session card's title and
/// the tournament subtitle are the parts that can only be written by a game that knows what a
/// board space is.
/// </remarks>
public sealed class KeystoneOverlayManager : OverlayManager
{
    private readonly Keystone _keystone;

    public KeystoneOverlayManager(Keystone keystone, IPubClip root)
        : base(keystone, root) => _keystone = keystone;

    /// <summary>Keystone counts a play as a roll of the dice.</summary>
    protected override int GetPlays() => _keystone.TotalRolls;

    /// <summary>
    /// The odds card: what every board space pays, at this table's rates.
    /// </summary>
    /// <remarks>
    /// THE RATES ARE THE TABLE'S, NOT THE GAME'S. Each of the 28 variations in
    /// <c>settings.xml</c> carries its own 46-entry payout array, so this has to read them back
    /// through <see cref="Keystone.GetPayoutRate"/> rather than print a constant table — Standard1
    /// pays 18:1 on OUT3 where the built-in default is 35:1.
    ///
    /// A bloodstone table states its odds differently, hence the two localisation keys.
    /// </remarks>
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

                    // GUARDED, WHERE THE SHIPPED CODE IS NOT. The AS2 divides by the bet
                    // unconditionally; a zero bet gives Infinity there and a DivideByZeroException
                    // here, so a space that stakes nothing prints 0 rather than taking the game
                    // down. No shipped table has a zero bet, which is why it never showed.
                    ["normalized10"] = bet == 0
                        ? "0"
                        : ((payout + bet) * 10 / bet).ToString(),
                });
        }
    }

    /// <summary>
    /// The session card names which kind of table this is.
    /// </summary>
    /// <remarks>
    /// <c>AllowJackpot</c> is the AS2's <c>Jackpot</c> getter, which returns <c>allowJackpot</c> —
    /// the table's own setting, NOT <see cref="Keystone.JackpotRolled"/>, which is whether this
    /// round happened to hit one. The title states what the player sat down at.
    /// </remarks>
    protected override void InitCashGameOverlay(IPubClip clip) =>
        Localize(
            clip.GetChild("title"),
            _keystone.Bloodstone
                ? "txtCashGameBloodstoneTitle"
                : _keystone.AllowJackpot
                    ? "txtCashGameJackpotTitle"
                    : "txtCashGameTitle",
            null);

    /// <summary>A tournament states how far through its rounds and rolls the player is.</summary>
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
