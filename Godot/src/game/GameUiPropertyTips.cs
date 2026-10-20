using System.Collections.Generic;
using System.Globalization;
using Refii.Host;
using Refii.Ui;

namespace Refii.Game;

/// <summary>
/// PROPERTY TIPS, Fable 3's (`TUTORIAL_FIRST_HOUSE`, `TUTORIAL_REAL_ESTATE_REPAIR_RENT_REMINDER`) in our
/// words, each told on the machine that plays its player and read from that player's own properties:
/// once, the first time a player owns a home, what a home is for and how condition and repair work
/// (kept as a saved player value, so it is once per hero); and, at most every ten minutes, which of their
/// rented homes are below half condition.
/// </summary>
/// <remarks>
/// THE HOST'S NOTICES ABOUT PLAYER TWO'S BUILDINGS ARE THE JOINER'S. A repair, a home falling into
/// disrepair or a shared purchase is decided on the host, where the notice is raised; online, one about a
/// building of player two's goes to the joiner (<c>property.notice=</c>) instead of being told to player one.
/// </remarks>
public partial class GameUi
{
	private const string FirstHomeKey = "refii.tip.firsthome";
	private const string PropertyNoticePrefix = "property.notice=";
	private const double PropertyTipPollSeconds = 2.0;
	private const double RepairReminderSeconds = 600.0;
	private const float RepairReminderCondition = 0.5f;
	private const float TipSeconds = 18f;

	private double _propertyTipPoll;
	private double _propertyClock;
	private readonly double[] _repairRemindedAt = { double.MinValue, double.MinValue, double.MinValue };

	private void DrivePropertyTips(double delta)
	{
		_propertyClock += delta;
		_propertyTipPoll -= delta;
		if (_propertyTipPoll > 0.0 || !HostBridge.IsInGame)
		{
			return;
		}

		_propertyTipPoll = PropertyTipPollSeconds;
		foreach (int player in LocalPlayers())
		{
			List<HostBridge.Property> properties = HostBridge.GetProperties(player);
			if (properties.Count == 0)
			{
				continue;
			}

			if (properties.Exists(property => property.IsHome) &&
				HostBridge.GetPlayerValue(player, FirstHomeKey) is null &&
				HostBridge.SetPlayerValue(player, FirstHomeKey, 1f))
			{
				Tip(player, "Your First Home",
					"A home is yours to live in, or to rent out for gold every few minutes. Rented homes wear " +
					"down each time rent is collected, and tenants stop paying at 0% condition. Check their " +
					"condition and repair them from the World Map's Properties list.");
			}

			var worn = new List<HostBridge.Property>();
			foreach (HostBridge.Property property in properties)
			{
				if (property.IsHome && property.IsRented && property.Condition < RepairReminderCondition)
				{
					worn.Add(property);
				}
			}

			if (worn.Count == 0 || _propertyClock - _repairRemindedAt[player] < RepairReminderSeconds)
			{
				continue;
			}

			_repairRemindedAt[player] = _propertyClock;
			worn.Sort((a, b) => a.Condition.CompareTo(b.Condition));
			HostBridge.Property worst = worn[0];
			string percent = ((int)System.MathF.Round(worst.Condition * 100f)).ToString(CultureInfo.InvariantCulture);
			string which = worn.Count == 1
				? $"{Engine_UiPropertySource.Name(worst)} is at {percent}% condition."
				: $"{worn.Count.ToString(CultureInfo.InvariantCulture)} of your rented homes need repairs; " +
					$"{Engine_UiPropertySource.Name(worst)} is at {percent}%.";
			Tip(player, "Repairs Needed",
				which + (worst.Condition <= 0f ? " Its tenants have stopped paying rent." : " Tenants stop paying rent at 0%.") +
				" You can repair houses via the map.");
		}
	}

	/// <summary>The players this machine plays: online, its own one; local co-op, both.</summary>
	private static IEnumerable<int> LocalPlayers()
	{
		if (CoopSession.LocalCoop)
		{
			yield return 1;
			yield return 2;
		}
		else
		{
			yield return CoopSession.PeerStanding && CoopSession.IsClient ? 2 : 1;
		}
	}

	private void Tip(int player, string title, string text)
	{
		if (CoopSession.LocalCoop)
		{
			title = $"Player {player.ToString(CultureInfo.InvariantCulture)}: {title}";
		}

		Godot.GD.Print($"[property-tip] player {player}: {title}");
		_boxes.InfoBoxParams("refii.propertytip", title, TipSeconds, text, 0,
			new Engine_UiGuiBoxes.BoxButtons(false, false, false, false, false, string.Empty, string.Empty));
	}

	/// <summary>
	/// A host property notice about one of player two's buildings, online: sent to the joiner, who
	/// plays them, and not told here. True when sent.
	/// </summary>
	private static bool ForwardPropertyNotice(int kind, ulong uid)
	{
		if (!CoopSession.PeerStanding || CoopSession.LocalCoop || CoopSession.IsClient || kind is 4 or 5)
		{
			return false;
		}

		// Repair All's outcome names its player in the packed value.
		if (kind == 7)
		{
			if (Engine_UiPropertySource.RepairAllPlayer(uid) != 2)
			{
				return false;
			}

			HostBridge.CoopSend(PropertyNoticePrefix + "7|" + uid.ToString("X16", CultureInfo.InvariantCulture));
			return true;
		}

		foreach (HostBridge.Property property in HostBridge.GetProperties(2))
		{
			if (property.Uid == uid)
			{
				HostBridge.CoopSend(PropertyNoticePrefix + kind.ToString(CultureInfo.InvariantCulture) + "|" +
					uid.ToString("X16", CultureInfo.InvariantCulture));
				return true;
			}
		}

		return false;
	}

	/// <summary>The host's property notice for this joiner's player.</summary>
	private bool TakePropertyNoticeMessage(string message)
	{
		if (!message.StartsWith(PropertyNoticePrefix, System.StringComparison.Ordinal))
		{
			return false;
		}

		string[] parts = message[PropertyNoticePrefix.Length..].Split('|');
		if (CoopSession.IsClient && parts.Length == 2 &&
			int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int kind) &&
			ulong.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong uid) &&
			Engine_UiPropertySource.Notice(kind, uid) is { } said)
		{
			_coopNotices.Tell(said);
		}

		return true;
	}
}
