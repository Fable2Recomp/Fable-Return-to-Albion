using System.Collections.Generic;
using Godot;
using Refii.Core;
using Refii.Host;
using Refii.Launcher.Core;

namespace Refii.Game;

/// <summary>
/// Discord Rich Presence: "Playing Fable 2: Return to Albion", with the level the player is
/// standing in — Bowerstone Market, Rookridge — as the first line.
/// </summary>
/// <remarks>
/// THE NODE OWNS DISCORD. RexNet speaks to the Discord client itself — the connection, the
/// party, the join secret, the join when a friend accepts, and the friendship that a join
/// makes — from the application id in the title's <c>rexnet.toml</c>. This node only states
/// the two lines Discord shows and whether a joiner is admitted right now, which are the
/// things the game knows and the node does not.
///
/// The place is <see cref="GameWhereabouts.Current"/>, the level the guest states it is in,
/// so the line is right on every transition that reloads a level. Joinable is the guest's own
/// word, read through <see cref="NetBridge.HostState"/>, so a Discord "Join" never lands on a
/// session that would refuse it.
/// </remarks>
public partial class GamePresence : Node
{
	/// <summary>How often the shown text is re-derived; the node sends a change at once.</summary>
	private const double PollSeconds = 2.0;

	private double _poll;

	private bool _enabled = true;

	private (string Details, string State, bool? Joinable)? _stated;

	public override void _Ready()
	{
		IReadOnlyDictionary<string, string> config = Engine_RefiiConfig.Read(out _);
		_enabled = Engine_RefiiConfig.Bool(config, "discord_presence", true);
		if (!_enabled)
		{
			GD.Print("[presence] discord_presence = false");
		}
	}

	public override void _ExitTree() => NetBridge.DiscordEnabled(false);

	public override void _Process(double delta)
	{
		_poll += delta;
		if (_poll < PollSeconds)
		{
			return;
		}

		_poll = 0;

		// Said every poll rather than once: the node comes up after this scene, and a word
		// that reached nothing is a word not said.
		NetBridge.DiscordEnabled(_enabled);
		if (!_enabled)
		{
			return;
		}

		(string Details, string State, bool? Joinable) wanted = Describe();
		if (_stated is { } stated && stated == wanted)
		{
			return;
		}

		NetBridge.DiscordActivity(wanted.Details, wanted.State, wanted.Joinable);
		_stated = wanted;
		GD.Print($"[presence] {wanted.Details} — {wanted.State}" +
			(wanted.Joinable == true ? " (joinable)" : string.Empty));
	}

	/// <summary>
	/// The two lines Discord shows under the game's name, and whether the game admits a joiner.
	/// </summary>
	private static (string Details, string State, bool? Joinable) Describe()
	{
		if (HostBridge.TryGetAbiVersion() is null)
		{
			return ("Starting up", string.Empty, false);
		}

		string details;
		string state;
		if (!HostBridge.IsInGame)
		{
			details = "At the main menu";
			state = string.Empty;
		}
		else if (GameWhereabouts.Current is { } place && place.DisplayName.Length > 0)
		{
			details = place.DisplayName;
			state = "Exploring Albion";
		}
		else
		{
			details = "Travelling";
			state = "Exploring Albion";
		}

		// The party is the session: offered only while the game admits a joiner.
		bool joinable = NetBridge.HostState() is { Hosting: true, Joinable: true };
		return (details, joinable ? "Co-op, open to join" : state, joinable);
	}
}
