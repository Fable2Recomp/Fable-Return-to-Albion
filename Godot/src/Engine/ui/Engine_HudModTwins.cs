using System.Collections.Generic;

namespace Refii.Ui;

/// <summary>
/// EACH PLAYER'S, FROM ONE RECORD: a copy or a piece that reads a <c>player:</c> value states
/// something every player has, so a copy of player one's widget (<c>PlayerHealth</c>,
/// <c>SpellCharging</c>) is built beside player two's (<c>CoopHealth</c>, <c>SpellCharging2</c>)
/// too wherever that one is drawn — the shared screen's partner widget — and a piece that
/// follows player one's widget follows player two's as well. The second is named
/// <see cref="PlayerTwoId"/> (the id with <c>2</c> after it, as the game names its own) and reads
/// player two's values under the same keys. A record that reads only <c>mod:</c> values (one
/// number for everyone) stays on the widget it names; a mod that states the player-two record
/// itself (that id) keeps its own.
/// </summary>
/// <remarks>Pure: the composition rules a mod's <c>ui/hud.json</c> records go through (Engine_HudModPieces.LoadFrom, Engine_HudView's copies), held by tests.</remarks>
public static class Engine_HudModTwins
{
	/// <summary>Whether a copy reads a <c>player:</c> value: it is each player's.</summary>
	public static bool EachPlayer(Engine_HudModPieces.Clone clone)
	{
		foreach (string source in clone.Values.Values)
		{
			if (ReadsPlayer(source))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>Whether a piece reads a <c>player:</c> value (<see cref="EachPlayer(Engine_HudModPieces.Clone)"/>).</summary>
	public static bool EachPlayer(Engine_HudModPieces.Piece piece) =>
		ReadsPlayer(piece.Value) || ReadsPlayer(piece.Max) || ReadsPlayer(piece.WidthFrom) ||
		ReadsPlayer(piece.Index) || ReadsPlayer(piece.Text);

	private static bool ReadsPlayer(string? source) =>
		source is { Length: > 0 } && source.Contains(Engine_UiValueSources.PlayerPrefix, System.StringComparison.Ordinal);

	/// <summary>The id of a record's player-two twin: the id with <c>2</c> after it.</summary>
	public static string PlayerTwoId(string id) => id + "2";

	/// <summary>
	/// <paramref name="clone"/> as a copy of player two's widget <paramref name="twoId"/>: its id
	/// <see cref="PlayerTwoId"/>, its meters and bars renamed through <paramref name="names"/>
	/// (player one's meter name → player two's; a name not in it stays).
	/// </summary>
	public static Engine_HudModPieces.Clone TwinForPlayerTwo(Engine_HudModPieces.Clone clone, string twoId, IReadOnlyDictionary<string, string> names)
	{
		string Named(string meter) => names.TryGetValue(meter, out string? two) ? two : meter;
		var values = new Dictionary<string, string>(System.StringComparer.Ordinal);
		foreach ((string meter, string source) in clone.Values)
		{
			values[Named(meter)] = source;
		}

		var bars = new Dictionary<string, string>(System.StringComparer.Ordinal);
		foreach ((string element, string meter) in clone.Bars)
		{
			bars[element] = Named(meter);
		}

		return clone with { Id = PlayerTwoId(clone.Id), Of = twoId, Values = values, Bars = bars };
	}

	/// <summary>
	/// The pieces with each <see cref="EachPlayer(Engine_HudModPieces.Piece)"/> piece that follows player one's widget
	/// joined by its twin following player two's, after it — unless a mod states that id itself.
	/// </summary>
	public static List<Engine_HudModPieces.Piece> WithPlayerTwoTwins(List<Engine_HudModPieces.Piece> pieces)
	{
		var ids = new HashSet<string>(System.StringComparer.Ordinal);
		foreach (Engine_HudModPieces.Piece piece in pieces)
		{
			ids.Add(piece.Id);
		}

		var composed = new List<Engine_HudModPieces.Piece>(pieces.Count);
		foreach (Engine_HudModPieces.Piece piece in pieces)
		{
			composed.Add(piece);
			if (piece.Follow.Length == 0 || !EachPlayer(piece) || ids.Contains(PlayerTwoId(piece.Id)))
			{
				continue;
			}

			var follows = new List<string>();
			foreach (string followed in piece.Follow.Split(',', System.StringSplitOptions.RemoveEmptyEntries |
				System.StringSplitOptions.TrimEntries))
			{
				if (Engine_HudLayout.PlayerTwoSourceId(followed) is { } two)
				{
					follows.Add(two);
				}
			}

			if (follows.Count != 0)
			{
				composed.Add(piece with { Id = PlayerTwoId(piece.Id), Follow = string.Join(',', follows) });
			}
		}

		return composed;
	}
}
