using System;
using System.Collections.Generic;

namespace Refii.Core;

/// <summary>
/// Named memory pools the in-game Tracy capture shows — what the engine holds that Godot does not
/// account per owner: the texture cache, the RenderingDevice targets, movies. Each allocation is
/// an id (an RID, a handle) and a size; its release names the same id.
/// </summary>
/// <remarks>
/// Inert unless the game's Tracy is on (<see cref="Sink"/> is installed by the session). The live
/// set is kept here as well, because a capture starts mid-session: <see cref="Replay"/> hands it
/// over when one starts, so the capture opens with what is actually held rather than only what is
/// made afterwards.
/// </remarks>
public static class Engine_TracedMemory
{
	/// <summary>Where allocations and releases go: the session's bridge to Tracy.</summary>
	public interface ISink
	{
		void Alloc(string pool, ulong id, ulong size);

		void Free(string pool, ulong id);
	}

	public static ISink? Sink;

	private static readonly object Gate = new();
	private static readonly Dictionary<string, Dictionary<ulong, ulong>> Pools = new();

	public static void Alloc(string pool, ulong id, ulong size)
	{
		if (Sink is not { } sink || id == 0)
		{
			return;
		}

		lock (Gate)
		{
			if (!Pools.TryGetValue(pool, out Dictionary<ulong, ulong>? live))
			{
				Pools[pool] = live = new Dictionary<ulong, ulong>();
			}

			if (live.ContainsKey(id))
			{
				sink.Free(pool, id);
			}

			live[id] = size;
		}

		sink.Alloc(pool, id, size);
	}

	public static void Free(string pool, ulong id)
	{
		if (Sink is not { } sink)
		{
			return;
		}

		lock (Gate)
		{
			if (!Pools.TryGetValue(pool, out Dictionary<ulong, ulong>? live) || !live.Remove(id))
			{
				return;
			}
		}

		sink.Free(pool, id);
	}

	/// <summary>A held allocation changed size in place (an image swapped into the same texture).</summary>
	public static void Resize(string pool, ulong id, ulong size)
	{
		lock (Gate)
		{
			if (!Pools.TryGetValue(pool, out Dictionary<ulong, ulong>? live) || !live.ContainsKey(id))
			{
				return;
			}
		}

		Alloc(pool, id, size);
	}

	/// <summary>Releases everything a pool holds (a cache cleared on a region change).</summary>
	public static void FreeAll(string pool)
	{
		if (Sink is not { } sink)
		{
			return;
		}

		ulong[] ids;
		lock (Gate)
		{
			if (!Pools.TryGetValue(pool, out Dictionary<ulong, ulong>? live))
			{
				return;
			}

			ids = new ulong[live.Count];
			live.Keys.CopyTo(ids, 0);
			live.Clear();
		}

		foreach (ulong id in ids)
		{
			sink.Free(pool, id);
		}
	}

	/// <summary>Reports every held allocation again: a capture has just started.</summary>
	public static void Replay()
	{
		if (Sink is not { } sink)
		{
			return;
		}

		var held = new List<(string, ulong, ulong)>();
		lock (Gate)
		{
			foreach ((string pool, Dictionary<ulong, ulong> live) in Pools)
			{
				foreach ((ulong id, ulong size) in live)
				{
					held.Add((pool, id, size));
				}
			}
		}

		foreach ((string pool, ulong id, ulong size) in held)
		{
			sink.Alloc(pool, id, size);
		}
	}
}
