namespace Refii.FlashGames.MageDice;

/// <summary>
/// What a reel symbol does when it lands.
/// </summary>
/// <remarks>
/// THE NUMBERS ARE THE TABLE'S, not this file's. Every variation in <c>magedice/settings.xml</c>
/// ships a <c>ReelTypes</c> array — nine entries for a nine-symbol table — stating each symbol's
/// type by these values, e.g. <c>[8,8,8,8,8,8,8,8,5]</c> for eight chain symbols and a bonus reel.
/// Renumbering them here would silently re-interpret every table in the game.
///
/// A 1:1 port of <c>Token.as</c>, which is eleven lines of constants and nothing else.
/// </remarks>
public static class Token
{
    /// <summary>Three of these unlock the bonus reel.</summary>
    public const int TypeBonusReel = 5;

    /// <summary>Each one raises the multiplier applied to the NEXT spin.</summary>
    public const int TypeBonusMult = 6;

    /// <summary>Pays in free spins rather than chips.</summary>
    public const int TypeFreeSpin = 7;

    /// <summary>The ordinary paying symbol: adjacent matches form a chain.</summary>
    public const int TypeChain = 8;

    public const int TypeBlank = 9;
}
