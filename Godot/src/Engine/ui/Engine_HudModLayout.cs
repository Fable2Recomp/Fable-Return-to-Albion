using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Refii.Ui;

/// <summary>
/// Where the game's OWN HUD pieces go, as mods move them: the <c>"layout"</c> block of a mod's
/// <c>ui/hud.json</c>, in EXACTLY the entry shape of the player's <c>user://hud/layout.json</c>
/// (<see cref="Engine_HudLayout.ApplyOverrides"/>), so one file's entries paste into the other.
/// </summary>
/// <remarks>
/// <code>
/// { "layout": { "group.PlayerHealth.PlayerHealth": { "anchor": [0, 1], "offset": [64, -96] } } }
/// </code>
/// <para>PRECEDENCE: the shipped arrangement, then every enabled mod's block lowest priority first
/// (a piece two mods move is the higher one's, and the pair is recorded as a conflict), then the
/// player's own file over all of them — the player's arrangement is theirs. Only <c>anchor</c> and
/// <c>offset</c> are read; the informational fields the editor writes beside them (<c>art</c>,
/// <c>value</c>, <c>driven</c>, <c>placed</c>) are ignored here as there.</para>
/// <para>Pure .NET over <see cref="System.Text.Json"/>, so the composition is held by tests.</para>
/// </remarks>
public static class Engine_HudModLayout
{
	/// <summary>The block's key in a mod's <c>ui/hud.json</c>.</summary>
	public const string Section = "layout";

	/// <summary>The block's key in the player's <c>user://hud/layout.json</c>.</summary>
	public const string PlayerSection = "pieces";

	/// <summary>One piece's place: the anchor fraction (0..1 each way) and the offset in reference units, +Y down.</summary>
	public readonly record struct Move(float AnchorX, float AnchorY, float OffsetX, float OffsetY);

	/// <summary>Two mods moving one piece: the higher-priority one won.</summary>
	public readonly record struct Conflict(string Piece, string Winner, string Loser);

	/// <summary>What the enabled mods state, composed: each piece's place and which mod gave it.</summary>
	public sealed record Composed(
		IReadOnlyDictionary<string, Move> Moves,
		IReadOnlyDictionary<string, string> Owners,
		IReadOnlyList<Conflict> Conflicts)
	{
		public static readonly Composed Empty = new(
			new Dictionary<string, Move>(), new Dictionary<string, string>(), Array.Empty<Conflict>());
	}

	/// <summary>
	/// The entries of one layout object (piece id → <c>{ "anchor": [x, y], "offset": [x, y] }</c>).
	/// An entry missing either pair, or with fewer than two numbers in one, is skipped — the rule
	/// the player's file is read by.
	/// </summary>
	public static Dictionary<string, Move> ParseEntries(JsonElement entries)
	{
		var moves = new Dictionary<string, Move>(StringComparer.Ordinal);
		if (entries.ValueKind != JsonValueKind.Object)
		{
			return moves;
		}

		foreach (JsonProperty entry in entries.EnumerateObject())
		{
			if (entry.Value.ValueKind == JsonValueKind.Object &&
				Pair(entry.Value, "anchor") is { } anchor &&
				Pair(entry.Value, "offset") is { } offset)
			{
				moves[entry.Name] = new Move(anchor.X, anchor.Y, offset.X, offset.Y);
			}
		}

		return moves;
	}

	/// <summary>A mod's <c>ui/hud.json</c> <c>"layout"</c> block; empty when it has none or is not JSON.</summary>
	public static Dictionary<string, Move> ParseModFile(string text) => ParseSection(text, Section);

	/// <summary>The player's <c>user://hud/layout.json</c> <c>"pieces"</c> object; empty when it has none.</summary>
	public static Dictionary<string, Move> ParsePlayerFile(string text) => ParseSection(text, PlayerSection);

	private static Dictionary<string, Move> ParseSection(string text, string section)
	{
		try
		{
			using JsonDocument document = JsonDocument.Parse(text);
			return document.RootElement.ValueKind == JsonValueKind.Object &&
				document.RootElement.TryGetProperty(section, out JsonElement entries)
					? ParseEntries(entries)
					: new Dictionary<string, Move>(StringComparer.Ordinal);
		}
		catch (JsonException)
		{
			return new Dictionary<string, Move>(StringComparer.Ordinal);
		}
	}

	/// <summary>
	/// Every mod's block, LOWEST priority first (the order <c>ModEntries.ReadEnabledText</c> gives),
	/// so the highest is applied last and wins a shared piece.
	/// </summary>
	public static Composed Compose(IEnumerable<(string Mod, string Text)> files)
	{
		var moves = new Dictionary<string, Move>(StringComparer.Ordinal);
		var owners = new Dictionary<string, string>(StringComparer.Ordinal);
		var conflicts = new List<Conflict>();
		foreach ((string mod, string text) in files)
		{
			foreach ((string piece, Move move) in ParseModFile(text))
			{
				if (owners.TryGetValue(piece, out string? previous) &&
					!string.Equals(previous, mod, StringComparison.OrdinalIgnoreCase))
				{
					conflicts.Add(new Conflict(piece, mod, previous));
				}

				moves[piece] = move;
				owners[piece] = mod;
			}
		}

		return new Composed(moves, owners, conflicts);
	}

	/// <summary>
	/// The moves that stand: the mods' composition with the player's file over it. A piece the
	/// player placed is the player's whatever the mods say.
	/// </summary>
	public static Dictionary<string, Move> Resolve(Composed mods, IReadOnlyDictionary<string, Move>? player)
	{
		var result = new Dictionary<string, Move>(mods.Moves, StringComparer.Ordinal);
		if (player is not null)
		{
			foreach ((string piece, Move move) in player)
			{
				result[piece] = move;
			}
		}

		return result;
	}

	private static (float X, float Y)? Pair(JsonElement record, string key)
	{
		if (!record.TryGetProperty(key, out JsonElement value) || value.ValueKind != JsonValueKind.Array ||
			value.GetArrayLength() < 2)
		{
			return null;
		}

		JsonElement x = value[0];
		JsonElement y = value[1];
		return x.ValueKind == JsonValueKind.Number && y.ValueKind == JsonValueKind.Number
			? ((float)x.GetDouble(), (float)y.GetDouble())
			: null;
	}
}
