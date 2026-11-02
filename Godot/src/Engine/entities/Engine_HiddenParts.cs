using System;
using System.Collections.Generic;
using System.Threading;

namespace Refii.Entities;

/// <summary>
/// The parts of entities a mod has hidden in the drawing — <c>refii_mod_hide_part</c> /
/// <c>refii_mod_hide_part_for</c>, <c>Refii.hide_part</c>, <c>RefiiHost.hide_part</c> — by entity
/// HANDLE, bone name and the player whose view loses it (0: every view).
/// </summary>
/// <remarks>
/// <para>GODOT OWNS THE SKELETON. <see cref="Engine_EntitySkeleton"/> builds it in the model's bone
/// table, so a part is a bone by the name the rig gives it (Godot's name, else the title's
/// <see cref="Refii.Animations.Engine_TitleRig.BoneName"/> of it) and is hidden where Godot draws it:
/// <see cref="Engine_HiddenPartModifier"/> draws the bone and every bone under it at nothing after the
/// game's pose, in every view or in one player's (<see cref="Engine_ViewHides"/>). The game's pose is
/// never written.</para>
/// <para>EACH STATEMENT STANDS ALONE. A part hidden for every view and the same part hidden for player
/// one are two statements: showing it for player one leaves the every-view hide standing.</para>
/// <para>KEPT BY THE HOST. The set outlives the entity's node: a rebuilt node gets the modifier again
/// from the entity walk (GameEntities), found by the handle. Cleared when the mods' Lua loads again
/// (lua_ready), as the claims and HUD overrides are. Pure state, held by tests without Godot.</para>
/// </remarks>
public static class Engine_HiddenParts
{
	private static readonly object Gate = new();
	private static readonly Dictionary<uint, Dictionary<string, int>> ByHandle = new();
	private static int _count;
	private static int _revision;

	/// <summary>How many entities have a part hidden in any view; the entity walk's cheap test.</summary>
	public static int Count => Volatile.Read(ref _count);

	/// <summary>Bumped on every change, so a modifier resolves its bones again.</summary>
	public static int Revision => Volatile.Read(ref _revision);

	/// <summary>
	/// Hides (<paramref name="on"/>) or shows <paramref name="part"/> of the entity <paramref name="handle"/>
	/// in <paramref name="player"/>'s view (1 or 2), or in every view (0); false for no handle, no name
	/// or any other player.
	/// </summary>
	public static bool Set(uint handle, string? part, bool on, int player = 0)
	{
		string name = (part ?? string.Empty).Trim();
		if (handle == 0 || name.Length == 0 || player is not (0 or 1 or 2))
		{
			return false;
		}

		int bit = 1 << player;
		lock (Gate)
		{
			if (on)
			{
				if (!ByHandle.TryGetValue(handle, out Dictionary<string, int>? parts))
				{
					parts = new Dictionary<string, int>(StringComparer.Ordinal);
					ByHandle[handle] = parts;
				}

				parts[name] = (parts.TryGetValue(name, out int views) ? views : 0) | bit;
			}
			else if (ByHandle.TryGetValue(handle, out Dictionary<string, int>? parts) && parts.TryGetValue(name, out int views))
			{
				views &= ~bit;
				if (views == 0)
				{
					parts.Remove(name);
				}
				else
				{
					parts[name] = views;
				}

				if (parts.Count == 0)
				{
					ByHandle.Remove(handle);
				}
			}

			Volatile.Write(ref _count, ByHandle.Count);
			Interlocked.Increment(ref _revision);
		}

		return true;
	}

	/// <summary>The parts hidden on <paramref name="handle"/> in every view, empty for none.</summary>
	public static string[] Of(uint handle) => Of(handle, 0);

	/// <summary>
	/// The parts hidden on <paramref name="handle"/> in <paramref name="player"/>'s view alone (1 or 2),
	/// or in every view (0); empty for none.
	/// </summary>
	public static string[] Of(uint handle, int player)
	{
		if (player is not (0 or 1 or 2))
		{
			return Array.Empty<string>();
		}

		int bit = 1 << player;
		lock (Gate)
		{
			if (!ByHandle.TryGetValue(handle, out Dictionary<string, int>? parts))
			{
				return Array.Empty<string>();
			}

			var named = new List<string>(parts.Count);
			foreach ((string name, int views) in parts)
			{
				if ((views & bit) != 0)
				{
					named.Add(name);
				}
			}

			return named.Count == 0 ? Array.Empty<string>() : [.. named];
		}
	}

	/// <summary>Every handle with a part hidden in any view.</summary>
	public static uint[] Handles()
	{
		lock (Gate)
		{
			return [.. ByHandle.Keys];
		}
	}

	/// <summary>Every part shown again: the mods' Lua loaded again.</summary>
	public static void Clear()
	{
		lock (Gate)
		{
			if (ByHandle.Count == 0)
			{
				return;
			}

			ByHandle.Clear();
			Volatile.Write(ref _count, 0);
			Interlocked.Increment(ref _revision);
		}
	}
}
