using System.Collections.Generic;
using Godot;
using Refii.Host;

namespace Refii.Game;

/// <summary>
/// This node's view of RexNet: its own peer and shard, the shard's live members, and every peer
/// holding a virtual address. RexNet has no server, so the view is one node's — the shard this
/// player is in, never the whole network. Names are RexNet's: a friend's own, anyone else's
/// pseudonym.
/// </summary>
public partial class GameDevTabNetwork : GameDevTab
{
	public override string Title => "Network";

	public override double RefreshSeconds => 1.0;

	private static readonly string[] States = { "offline", "online", "in game", "joinable" };
	private static readonly string[] Statuses = { "idle", "pending", "connected", "lost" };

	private readonly Label _summary = Readout();

	private readonly GameDevTable _shard =
		new(new[] { "player", "friend", "state", "session", "peer" });

	private readonly GameDevTable _links =
		new(new[] { "player", "address", "status", "path", "control", "friend", "relay", "ping ms", "peer" });

	public GameDevTabNetwork()
	{
		AddChild(_summary);
		_shard.SetColumnWidth(3, 280);
		_shard.SetColumnWidth(4, 460);
		_links.SetColumnWidth(1, 130);
		_links.SetColumnWidth(8, 460);

		// Stacked, not side by side: a peer id is 52 characters and each table needs the width.
		AddChild(Caption("Shard — players heard in the last 95 s"));
		AddChild(_shard);
		AddChild(Caption("Links — peers holding a virtual address"));
		AddChild(_links);
	}

	private static Label Caption(string text) =>
		new() { Text = text, Modulate = new Color(0.7f, 0.85f, 1f) };

	public override void Refresh()
	{
		if (NetBridge.Network() is not { } view)
		{
			_summary.Text = "RexNet is not up.";
			_shard.Show(new List<string[]>());
			_links.Show(new List<string[]>());
			return;
		}

		NetBridge.NetworkSelf self = view.Self;
		int connected = 0;
		var linkRows = new List<string[]>(view.Links.Count);
		foreach (NetBridge.Link link in view.Links)
		{
			if (link.Status == 2)
			{
				++connected;
			}

			linkRows.Add(new[]
			{
				link.Name,
				Address(link.VirtualIp),
				Named(Statuses, link.Status),
				link.Status != 2 ? "" : link.Degraded ? "tunnel" : "punched",
				link.ControlLinked ? "up" : "",
				link.IsFriend ? "yes" : "",
				link.IsRelay ? "yes" : "",
				link.RttMs == 0 ? "" : link.RttMs.ToString(),
				link.PeerId,
			});
		}

		var shardRows = new List<string[]>(view.Shard.Count);
		foreach (NetBridge.ShardMember member in view.Shard)
		{
			shardRows.Add(new[]
			{
				member.Name,
				member.IsFriend ? "yes" : "",
				Named(States, member.State),
				member.SessionId,
				member.PeerId,
			});
		}

		_summary.Text =
			$"peer    {self.PeerId}\n" +
			$"title   {self.TitleId:X8}    address {Address(self.VirtualIp)}\n" +
			$"shard   {(self.ShardId.Length == 0 ? "none" : self.ShardId)}    " +
			$"{view.Shard.Count} members    links {connected} connected of {view.Links.Count}";
		_shard.Show(shardRows);
		_links.Show(linkRows);
	}

	private static string Named(string[] names, int index) =>
		index >= 0 && index < names.Length ? names[index] : index.ToString();

	private static string Address(uint ip) =>
		$"{ip >> 24}.{(ip >> 16) & 0xFF}.{(ip >> 8) & 0xFF}.{ip & 0xFF}";
}
