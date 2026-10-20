using System.Collections.Generic;
using System.Globalization;
using Refii.Host;

namespace Refii.Ui;

/// <summary>
/// THE WORLD MAP'S PROPERTIES: the properties of the player whose menus are open, each with its
/// condition, and Fable 3's repair from the map (its `REPAIR_RENT_REMINDER`: "You can repair houses
/// via the map"). Fable 2 has no condition; the native side keeps Fable 3's
/// (hooks/src/property_hooks.cpp, core/property_condition.h): rented homes lose 0.01 a rent drop,
/// pay nothing at 0, and a full repair costs half the value lost.
/// </summary>
/// <remarks>
/// FABLE 3'S PARTNERSHIPS head the list while both heroes are in the game: a proposal of marriage
/// or an offer of business partnership (the other player is asked, <see cref="AskPartner"/>),
/// divorce or the partnership's end (the asker's alone). Partners share what either buys from then
/// on — its rent and sale split half and half (core/hero_partners.cpp, property_hooks.cpp) — and
/// a shared property says so here.
/// </remarks>
public static class Engine_UiPropertySource
{
	public const string SourceName = "properties";

	/// <summary>`PropertyRepair:&lt;uid hex&gt;` — a row's repair, confirmed first.</summary>
	public const string RepairAction = "PropertyRepair:";

	/// <summary>The co-op channel message an online joiner sends the host for its repair.</summary>
	public const string RepairMessage = "property.repair=";

	/// <summary>`Partner:&lt;propose|business|divorce|end&gt;` — a partnership row.</summary>
	public const string PartnerAction = "Partner:";

	/// <summary>Consent kinds for the partner's question (GameUiCoopConsent): a proposal, an offer.</summary>
	public const int ProposalKind = 3;
	public const int BusinessKind = 4;

	/// <summary>Asks the other player (a consent kind above); set by the game's UI.</summary>
	public static System.Action<int>? AskPartner { get; set; }

	private const string Icon = @"Art\GUI\WheelIcons\icon_logbook.tga";

	/// <summary>The last list read, by UID, for the confirm box and the notices.</summary>
	private static readonly Dictionary<ulong, HostBridge.Property> _known = new();

	public static IReadOnlyList<Engine_UiMenuSources.Row> Rows(string argument)
	{
		var rows = new List<Engine_UiMenuSources.Row>();
		if (HostBridge.GetPartnership() is { } partnership)
		{
			if (partnership < 2)
			{
				rows.Add(PartnerRow("propose", "Propose marriage to your partner"));
			}

			rows.Add(partnership switch
			{
				0 => PartnerRow("business", "Offer your partner a business partnership"),
				1 => PartnerRow("end", "End your business partnership"),
				_ => PartnerRow("divorce", "Divorce your partner"),
			});
		}

		foreach (HostBridge.Property property in HostBridge.GetProperties(0))
		{
			_known[property.Uid] = property;
			string name = Name(property);
			bool decays = property.IsHome && property.IsRented;
			string label = decays || property.Condition < 1f
				? $"{name} — {Percent(property.Condition)} condition"
				: name;
			if (property.IsShared)
			{
				label += " — shared with your partner";
			}
			bool repairable = property.Condition < 1f && property.RepairCost > 0;
			rows.Add(new Engine_UiMenuSources.Row(
				Name: $"Property:{property.Uid:X16}",
				Label: repairable ? $"{label} — Repair {property.RepairCost.ToString(CultureInfo.InvariantCulture)} gold" : label,
				Icon: Icon,
				EventType: 0,
				Id: 0u,
				Rating: Engine_UiMenuConstants.EmptyRatingSlide,
				Committable: repairable,
				Ghosted: !repairable,
				Command: repairable ? RepairAction + property.Uid.ToString("X16", CultureInfo.InvariantCulture) : null));
		}

		return rows;
	}

	private static Engine_UiMenuSources.Row PartnerRow(string verb, string label) => new(
		Name: PartnerAction + verb,
		Label: label,
		Icon: Icon,
		EventType: 0,
		Id: 0u,
		Rating: Engine_UiMenuConstants.EmptyRatingSlide,
		Committable: true,
		Ghosted: false,
		Command: PartnerAction + verb);

	/// <summary>A property's display name: its BuildingName tag through the string book.</summary>
	public static string Name(HostBridge.Property property) =>
		Engine_UiInventorySource.Strings is { } book && property.NameTag.Length != 0 &&
		book.TryGetString(property.NameTag, out string resolved) && resolved.Length != 0
			? resolved
			: property.NameTag.Length != 0 ? property.NameTag : "A property";

	/// <summary>The name of a property seen in the last list, for a notice.</summary>
	public static string NameOf(ulong uid) =>
		_known.TryGetValue(uid, out HostBridge.Property property) ? Name(property) : "one of your properties";

	private static string Percent(float condition) =>
		((int)System.MathF.Round(condition * 100f)).ToString(CultureInfo.InvariantCulture) + "%";

	/// <summary>
	/// A row's repair: Fable 3's confirmation ("Do you want to fully repair this house? It will
	/// cost %1 gold."), then the repair for the player whose menus are open — on the host's world,
	/// which an online joiner reaches over the co-op channel.
	/// </summary>
	public static bool Run(string action)
	{
		if (action.StartsWith(PartnerAction, System.StringComparison.Ordinal))
		{
			RunPartner(action[PartnerAction.Length..]);
			return true;
		}

		if (!action.StartsWith(RepairAction, System.StringComparison.Ordinal) ||
			!ulong.TryParse(action[RepairAction.Length..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong uid))
		{
			return false;
		}

		int cost = _known.TryGetValue(uid, out HostBridge.Property property) ? property.RepairCost : 0;
		string uidText = uid.ToString("X16", CultureInfo.InvariantCulture);
		Engine_UiHostBoxes.Ask(
			$"Do you want to fully repair {NameOf(uid)}? It will cost {cost.ToString(CultureInfo.InvariantCulture)} gold.",
			() =>
			{
				if (CoopSession.IsClient)
				{
					HostBridge.CoopSend(RepairMessage + uidText);
					return;
				}

				int player = CoopSession.LocalCoop && HostBridge.GetMenuOwner() == 2 ? 2 : 1;
				HostBridge.PostFrontEndAction($"Property:repair:{player}:{uidText}");
			},
			null,
			"property:repair:" + uidText);
		return true;
	}

	/// <summary>
	/// A partnership row: a proposal or an offer goes to the other player as a question; divorce and
	/// the partnership's end are confirmed and done.
	/// </summary>
	private static void RunPartner(string verb)
	{
		switch (verb)
		{
			case "propose":
				Engine_UiHostBoxes.Ask("Ask your partner to marry you?", () => AskPartner?.Invoke(ProposalKind), null,
					"partner:propose");
				break;
			case "business":
				Engine_UiHostBoxes.Ask(
					"Offer your partner a business partnership? You will share every property either of you buys from now on, and its rent.",
					() => AskPartner?.Invoke(BusinessKind), null, "partner:business");
				break;
			case "divorce":
			case "end":
				bool divorce = verb == "divorce";
				Engine_UiHostBoxes.Ask(
					divorce
						? "Divorce your partner? You will no longer share property."
						: "End your business partnership? You will no longer share property.",
					() =>
					{
						if (HostBridge.SetPartnership(0))
						{
							Told?.Invoke(divorce ? "You are divorced." : "Your business partnership has ended.");
						}
					},
					null,
					"partner:" + verb);
				break;
		}
	}

	/// <summary>Tells the player something (the game's notices); set by the game's UI.</summary>
	public static System.Action<string>? Told { get; set; }

	/// <summary>The words for a property notice (Fable 3's, where it has them).</summary>
	public static string? Notice(int kind, ulong uid) => kind switch
	{
		0 => $"Your tenants in {NameOf(uid)} are refusing to pay rent because the house has fallen into disrepair. You can repair houses via the map.",
		1 => $"{NameOf(uid)} has been fully repaired.",
		2 => "You don't have enough gold to repair this house.",
		4 => $"While you were away, your share of the rent and sales from your shared properties came to {uid.ToString(CultureInfo.InvariantCulture)} gold.",
		5 => $"Half of the sale, {uid.ToString(CultureInfo.InvariantCulture)} gold, goes to your partner.",
		6 => $"You and your partner now share {NameOf(uid)}.",
		_ => null,
	};
}
