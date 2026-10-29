namespace Refii.Mods;

/// <summary>
/// The mod SDK's UI files whose schema the validator holds and a title's UI reads: where they sit
/// in a package and the tokens their values use.
/// </summary>
public static class Engine_ModUiFiles
{
	/// <summary>A mod's spell-wheel picks: a secondary pick on the held wheel and the stores it writes.</summary>
	public const string SpellWheelPath = "ui/spellwheel.json";

	/// <summary>A pick's store that is the slot's own spell — the game's pick, not a mod's value.</summary>
	public const string GameStore = "game";

	/// <summary>The token a pick's store names its slot by.</summary>
	public const string SlotToken = "{slot}";

	/// <summary>A mod's abilities on the abilities book.</summary>
	public const string AbilitiesPath = "ui/abilities.json";

	/// <summary>The shipped unlearn's refund: half the price back.</summary>
	public const string HalfRefund = "half";
}
