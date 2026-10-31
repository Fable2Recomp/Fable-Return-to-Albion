using Refii.Host;

namespace Refii.Ui;

/// <summary>
/// The gamer card an orb's Interact asks for, as the one question it comes down to here. On the
/// console the orb box's Interact opened the guide's card for that player, and the card's rows
/// were Join Game and Invite to Game; the guide is not ours to draw, so the card is the game's own
/// question box asking whichever of the two applies (<c>GameUi</c> raises it).
/// </summary>
/// <remarks>
/// PURE: what to offer and what to say, from the card the node resolved
/// (<see cref="NetBridge.LatestGamerCard"/>) and whether this game has a seat to offer.
/// </remarks>
public static class Engine_UiGamerCard
{
	public enum Offer
	{
		/// <summary>The XUID matched no player the node knows (they left, or the link is down).</summary>
		Unknown,

		/// <summary>They are already in this game.</summary>
		Partner,

		/// <summary>They are hosting: join their game.</summary>
		Join,

		/// <summary>They are not hosting and this game has a seat: invite them into it.</summary>
		Invite,

		/// <summary>Neither side has a game for the other.</summary>
		Nothing,
	}

	/// <summary>
	/// What the card offers. <paramref name="canInvite"/> is whether this game would take them: we
	/// host it and nobody else is in it.
	/// </summary>
	public static Offer Decide(NetBridge.GamerCard card, bool canInvite)
	{
		if (!card.Known)
		{
			return Offer.Unknown;
		}

		if (card.Partner)
		{
			return Offer.Partner;
		}

		if (card.Hosting)
		{
			return Offer.Join;
		}

		return canInvite ? Offer.Invite : Offer.Nothing;
	}

	/// <summary>Whether the offer is a question (A acts) rather than a notice.</summary>
	public static bool Asks(Offer offer) => offer is Offer.Join or Offer.Invite;

	/// <summary>The question's or notice's text.</summary>
	public static string Text(Offer offer, string name) => offer switch
	{
		Offer.Join => $"Join {name}'s game?\n\nJoining leaves this one, and anything you have not saved is lost.",
		Offer.Invite => $"{name} is not hosting a game. Invite them to join yours?",
		Offer.Partner => $"{name} is already playing in your game.",
		Offer.Nothing => $"{name} is not hosting a game you can join right now.",
		_ => "That player cannot be reached right now.",
	};

	/// <summary>The question's A caption.</summary>
	public static string? Accept(Offer offer) => offer switch
	{
		Offer.Join => "Join Game",
		Offer.Invite => "Invite to Game",
		_ => null,
	};

	/// <summary>What is told once A was pressed and the node took (or refused) the action.</summary>
	public static string Outcome(Offer offer, string name, bool sent) => (offer, sent) switch
	{
		(Offer.Join, true) => $"Joining {name}'s game. It loads when the host answers.",
		(Offer.Join, false) => $"{name} is not hosting a game you can join right now.",
		(Offer.Invite, true) => $"Invitation sent to {name}.",
		_ => $"{name} could not be invited right now.",
	};
}
