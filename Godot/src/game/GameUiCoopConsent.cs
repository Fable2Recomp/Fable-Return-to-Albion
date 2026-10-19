using System.Collections.Generic;
using System.Globalization;
using Godot;
using Refii.Host;
using Refii.Ui;

namespace Refii.Game;

/// <summary>
/// PARTNER CONSENT, Fable 3's bed rule: sleeping moves the clock for both players and sex fades
/// both screens, so the other player is asked first (Fable 3 `sub_825311B0` / `sub_825314C8`, the
/// `*_REJECTED_BY_P2` answers). A co-op script asks (core Lua, `Refii._consent_ask`) and waits; the
/// question comes here through the host bridge (core/coop_consent.cpp).
/// </summary>
/// <remarks>
/// THE PARTNER ANSWERS ON THEIR OWN SCREEN. In local co-op the question is a host question owned by
/// the partner's pad (and on split screen it stands in their half: <see cref="StatedMenuOwner"/>).
/// Online, whichever machine plays the partner shows it: a question for the other machine's player
/// goes over the co-op channel as STATE (<c>consent.ask=</c>, restated until answered) and the answer
/// comes back the same way (<c>consent.answer=</c>, restated a few times). A refusal is told to the
/// asker. A question nobody answers in <see cref="ConsentTimeoutSeconds"/> is a refusal.
/// A mod asks its own question (<c>Refii.ask_partner</c>, kind 0): its words are the box's, and
/// travel last on the ask message (they may hold colons). With no partner in the game, every
/// question is answered yes at once.
/// </remarks>
public partial class GameUi
{
	private const string ConsentAskPrefix = "consent.ask=";
	private const string ConsentAnswerPrefix = "consent.answer=";
	private const double ConsentTimeoutSeconds = 30.0;
	private const double ConsentRestateSeconds = 0.5;
	private const int ConsentAnswerRestates = 4;

	private sealed class ConsentAsk
	{
		public uint Ticket;
		public int Kind;
		public int Asker;
		public int Detail;
		public string Text = string.Empty;
		public double Age;
		public double Restate;

		/// <summary>Asked of the peer's player over the channel (this machine runs the script).</summary>
		public bool Remote;

		/// <summary>Asked on this machine (a box up for this machine's player).</summary>
		public bool Shown;

		/// <summary>Sent from the peer: answered here, the answer restated back.</summary>
		public bool FromPeer;

		public int Answer = -1;
		public int AnswerRestates;
	}

	private readonly List<ConsentAsk> _consentAsks = new();

	/// <summary>The local player who owns the consent box up, 0 when none (local co-op routing).</summary>
	private int _consentOwner;

	/// <summary>Whether this machine plays <paramref name="player"/>'s hero (the player store's rule).</summary>
	private static bool PlaysHere(int player) =>
		!CoopSession.PeerStanding || CoopSession.LocalCoop || (CoopSession.IsClient ? player == 2 : player == 1);

	private static string ConsentQuestion(int kind, int detail, string text) => kind switch
	{
		0 when text.Length != 0 => text,
		1 => "Your partner wants to sleep " + (detail switch
		{
			1 => "for six hours",
			2 => "for twelve hours",
			3 => "for a day",
			4 => "for a week",
			_ => "a while",
		}) + ". Is that all right?",
		2 => "Your partner wants to take someone to bed. Is that all right?",
		_ => "Your partner asks for your consent. Is that all right?",
	};

	private static string ConsentRefused(int kind) => kind switch
	{
		1 => "Your partner doesn't want to sleep now.",
		2 => "Your partner would rather you didn't.",
		_ => "Your partner said no.",
	};

	/// <summary>Takes the scripts' questions and keeps every one moving. Called once a frame.</summary>
	private void DriveCoopConsent(double delta)
	{
		while (HostBridge.ConsentTake() is { } taken)
		{
			var ask = new ConsentAsk
			{
				Ticket = taken.Ticket, Kind = taken.Kind, Asker = taken.Asker, Detail = taken.Detail, Text = taken.Text,
			};
			int partner = taken.Asker == 2 ? 1 : 2;
			if (!CoopSession.LocalCoop && !CoopSession.PeerStanding)
			{
				// Nobody to ask: no partner objects.
				HostBridge.ConsentGive(taken.Ticket, true);
				continue;
			}

			ask.Remote = !PlaysHere(partner);
			_consentAsks.Add(ask);
			GD.Print($"[coop-consent] player {taken.Asker} asks player {partner} (kind {taken.Kind}, " +
				$"detail {taken.Detail}){(ask.Remote ? " — over the channel" : string.Empty)}");
		}

		for (int i = _consentAsks.Count - 1; i >= 0; --i)
		{
			ConsentAsk ask = _consentAsks[i];
			ask.Age += delta;
			if (ask.FromPeer)
			{
				DrivePeerConsent(ask, delta);
				if (ask.Answer >= 0 && ask.AnswerRestates >= ConsentAnswerRestates)
				{
					_consentAsks.RemoveAt(i);
				}

				continue;
			}

			if (ask.Answer < 0 && ask.Age > ConsentTimeoutSeconds)
			{
				ask.Answer = 0;
				GD.Print($"[coop-consent] question {ask.Ticket} lapsed unanswered — a refusal");
			}

			if (ask.Answer >= 0)
			{
				Engine_UiHostBoxes.Withdraw(ConsentKey(ask.Ticket));
				if (_consentOwner != 0 && !Engine_UiHostBoxes.Asking(ConsentKey(ask.Ticket)))
				{
					_consentOwner = 0;
				}

				HostBridge.ConsentGive(ask.Ticket, ask.Answer == 1);
				if (ask.Answer == 0)
				{
					_coopNotices.Tell(ConsentRefused(ask.Kind));
				}

				_consentAsks.RemoveAt(i);
				continue;
			}

			if (ask.Remote)
			{
				ask.Restate -= delta;
				if (ask.Restate <= 0.0 && CoopSession.PeerStanding)
				{
					ask.Restate = ConsentRestateSeconds;
					HostBridge.CoopSend(string.Create(CultureInfo.InvariantCulture,
						$"{ConsentAskPrefix}{ask.Ticket}:{ask.Kind}:{ask.Asker}:{ask.Detail}:{ask.Text}"));
				}
			}
			else if (!ask.Shown && Engine_UiHostBoxes.Question is null)
			{
				ShowConsent(ask, ask.Asker == 2 ? 1 : 2);
			}
		}
	}

	/// <summary>A question from the peer's script, for this machine's player: shown once, answer restated.</summary>
	private void DrivePeerConsent(ConsentAsk ask, double delta)
	{
		if (ask.Answer < 0)
		{
			if (!ask.Shown && Engine_UiHostBoxes.Question is null)
			{
				ShowConsent(ask, ask.Asker == 2 ? 1 : 2);
			}

			return;
		}

		ask.Restate -= delta;
		if (ask.Restate <= 0.0)
		{
			ask.Restate = ConsentRestateSeconds;
			++ask.AnswerRestates;
			HostBridge.CoopSend(string.Create(CultureInfo.InvariantCulture,
				$"{ConsentAnswerPrefix}{ask.Ticket}:{ask.Answer}"));
		}
	}

	private void ShowConsent(ConsentAsk ask, int partner)
	{
		ask.Shown = true;
		_consentOwner = CoopSession.LocalCoop ? partner : 0;
		Engine_UiHostBoxes.Ask(
			ConsentQuestion(ask.Kind, ask.Detail, ask.Text),
			() => AnswerConsent(ask, true),
			() => AnswerConsent(ask, false),
			ConsentKey(ask.Ticket));
	}

	private void AnswerConsent(ConsentAsk ask, bool yes)
	{
		ask.Answer = yes ? 1 : 0;
		ask.Restate = 0.0;
		_consentOwner = 0;
		GD.Print($"[coop-consent] question {ask.Ticket} answered {(yes ? "yes" : "no")}");
	}

	private static string ConsentKey(uint ticket) => "consent:" + ticket.ToString(CultureInfo.InvariantCulture);

	/// <summary>
	/// A consent message from the peer: its script's question for this machine's player, or this
	/// machine's script's answer. False for anything else.
	/// </summary>
	private bool TakeConsentMessage(string message)
	{
		if (message.StartsWith(ConsentAskPrefix, System.StringComparison.Ordinal))
		{
			string[] parts = message[ConsentAskPrefix.Length..].Split(':', 5);
			if (parts.Length >= 4 &&
				uint.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint ticket) &&
				int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int kind) &&
				int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int asker) &&
				int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int detail) &&
				!_consentAsks.Exists(a => a.FromPeer && a.Ticket == ticket))
			{
				_consentAsks.Add(new ConsentAsk
				{
					Ticket = ticket, Kind = kind, Asker = asker, Detail = detail, FromPeer = true,
						Text = parts.Length == 5 ? parts[4] : string.Empty,
				});
			}

			return true;
		}

		if (message.StartsWith(ConsentAnswerPrefix, System.StringComparison.Ordinal))
		{
			string[] parts = message[ConsentAnswerPrefix.Length..].Split(':');
			if (parts.Length == 2 &&
				uint.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint ticket) &&
				int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int answer) &&
				_consentAsks.Find(a => !a.FromPeer && a.Remote && a.Ticket == ticket) is { } ask &&
				ask.Answer < 0)
			{
				ask.Answer = answer == 1 ? 1 : 0;
				GD.Print($"[coop-consent] question {ticket} answered {(ask.Answer == 1 ? "yes" : "no")} by the peer");
			}

			return true;
		}

		return false;
	}
}
