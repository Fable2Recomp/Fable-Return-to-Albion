namespace Refii.Mods;

/// <summary>
/// WHICH MOD'S SCREEN STANDS WHERE THE PAUSE BOOK WOULD (<c>RefiiHost.own_screen</c>): one owner at a
/// time — the MOD that took it, inside the mod set that runs it (its <see cref="RefiiHost"/>), and the
/// player it serves. The mod is the engine's own record of the caller: a Godot script's node carries the
/// <c>refii_mod</c> meta <see cref="ModGodot"/> stamps on it, a Lua ask the mod block it was composed in.
/// Released by that mod's <c>own_screen(player, false)</c>, never another's; and by the engine when the mod
/// set stops or reloads (a mod enabled or disabled), the game VM's Lua reloads (a screen a Lua mod took),
/// the game is left, or a level unloads — so a screen whose mod is gone can never leave the book hidden
/// and the pad held. A later take, by any mod, takes it over.
/// </summary>
public static class ModScreen
{
	private static object? _set;

	/// <summary>Whether a mod's screen stands.</summary>
	public static bool Owned => _set is not null;

	/// <summary>The mod whose screen stands; empty for a script that did not name itself, or with none.</summary>
	public static string Mod { get; private set; } = string.Empty;

	/// <summary>The player (1 or 2) the standing screen serves; 0 with none.</summary>
	public static int Player { get; private set; }

	/// <summary>Whether the standing screen was taken from the game VM's Lua (<c>Refii.ask_host</c>).</summary>
	public static bool FromLua { get; private set; }

	/// <summary>Takes the screen for <paramref name="mod"/> of the set <paramref name="set"/> and <paramref name="player"/>; a later owner takes it over.</summary>
	public static void Take(object set, string mod, int player, bool fromLua = false)
	{
		_set = set;
		Mod = mod ?? string.Empty;
		Player = player is 1 or 2 ? player : 1;
		FromLua = fromLua;
	}

	/// <summary>Releases the screen if <paramref name="mod"/> of <paramref name="set"/> holds it for <paramref name="player"/>.</summary>
	public static void Release(object set, string mod, int player)
	{
		if (ReferenceEquals(_set, set) && string.Equals(Mod, mod ?? string.Empty, System.StringComparison.Ordinal) && Player == player)
		{
			Release();
		}
	}

	/// <summary>Releases whatever the set <paramref name="set"/> holds: it stopped or reloaded.</summary>
	public static void ReleaseFor(object set)
	{
		if (ReferenceEquals(_set, set))
		{
			Release();
		}
	}

	/// <summary>Releases a screen a Lua mod of <paramref name="set"/> took: the game VM's Lua was replaced.</summary>
	public static void ReleaseLua(object set)
	{
		if (ReferenceEquals(_set, set) && FromLua)
		{
			Release();
		}
	}

	/// <summary>Releases the screen whoever holds it: the game left, a level unloading.</summary>
	public static void Release()
	{
		_set = null;
		Mod = string.Empty;
		Player = 0;
		FromLua = false;
	}
}
