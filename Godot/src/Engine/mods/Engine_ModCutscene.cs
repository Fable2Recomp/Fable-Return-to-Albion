namespace Refii.Mods;

/// <summary>
/// The cutscene statements as the bus last stated them (the <c>cutscene</c> event, or an
/// <c>in_cutscene</c> query's answer), for <see cref="RefiiHost.in_cutscene"/>. Each statement
/// carries <c>kinds</c>, every REFII_CUTSCENE_* bit holding for its player after the edge: the
/// full cutscene's bit is every player's, the interactive and look-at bits are that player's own.
/// </summary>
public sealed class ModCutscene
{
	/// <summary>[0] the full cutscene's bit; [1] and [2] each player's own bits.</summary>
	private readonly int[] _kinds = new int[3];

	/// <summary>
	/// A statement for player 0 (the full cutscene's edge: <paramref name="kinds"/> is its bit) or
	/// for player 1 or 2 (every bit holding for that player, the full cutscene's included).
	/// Any other player is ignored.
	/// </summary>
	public void Stated(long player, int kinds)
	{
		if (player is < 0 or > 2)
		{
			return;
		}

		_kinds[0] = kinds & ModApi.CutsceneFull;
		if (player != 0)
		{
			_kinds[player] = kinds & ~ModApi.CutsceneFull;
		}
	}

	/// <summary>The bits holding for player 1 or 2; any other player: the union over both.</summary>
	public int Kinds(int player) => player is 1 or 2
		? _kinds[0] | _kinds[player]
		: _kinds[0] | _kinds[1] | _kinds[2];

	/// <summary>Whether any of <paramref name="wanted"/> holds for the player (<see cref="Kinds"/>).</summary>
	public bool In(int player, int wanted = ModApi.CutsceneAll) => (Kinds(player) & wanted) != 0;
}
