using System;
using System.Collections.Generic;

namespace Refii.Host;

/// <summary>
/// What each address last stated, for <see cref="HostBridge.GuestEntity.Changed"/>: any stated word
/// that differs, bit for bit, from what the address last stated. Answered from the same row of the
/// previous walk where the list has not moved, and from the tables where it has.
/// </summary>
/// <remarks>
/// The guest's entity list keeps its order from walk to walk, so nearly every row stands where it
/// stood or a few places on. A row that matches the last walk's row for its address has stated
/// nothing new: that row either wrote the tables or matched them. Only a row with no such match
/// asks the tables, which also remember an address that left the walk for a while.
/// </remarks>
public sealed class HostStatedWords
{
	/// <summary>Words per row, and those the comparison reads (see the row layout in HostBridge).</summary>
	public const int Stride = 29;

	private const int AddressWord = 8;

	/// <summary>
	/// Placement and model (0..7), address (8), unique id (9, 10) and record (11): one span. The
	/// unique id is what tells a recycled address from the entity that held it before, so a new
	/// entity at an old address is a change even where its placement and model happen to match.
	/// </summary>
	private const int IdentityWords = 12;
	private const int LookFirst = 20;
	private const int LookWords = 9;

	/// <summary>How far ahead of the cursor a row is looked for: entities that left since the last walk.</summary>
	private const int Lookahead = 8;

	private const int PruneWalks = 600;

	private readonly Dictionary<uint, (uint X, uint Y, uint Z, uint Qx, uint Qy, uint Qz, uint Qw, uint Model, uint IdHigh, uint IdLow, uint Record)> _stated = new();
	private readonly Dictionary<uint, (uint Highlight, uint A0, uint A1, uint A2, uint A3, uint A4, uint A5, uint A6, uint A7)> _look = new();

	private uint[] _filling;
	private uint[] _previous;
	private int _previousCount;
	private int _cursor;
	private int _walksSincePrune;
	private HashSet<uint>? _seen;

	/// <param name="rows">The most rows one walk writes.</param>
	public HostStatedWords(int rows)
	{
		// Sized for the biggest region and read only up to the rows a walk wrote: not zeroed, so the
		// pages past a town's rows are never touched.
		_filling = GC.AllocateUninitializedArray<uint>(rows * Stride);
		_previous = GC.AllocateUninitializedArray<uint>(rows * Stride);
	}

	/// <summary>The buffer this walk's rows go in.</summary>
	public uint[] Words => _filling;

	/// <summary>The walk's rows are in <see cref="Words"/>; read them in order with <see cref="Changed"/>.</summary>
	public void BeginWalk(int count)
	{
		_cursor = 0;
		_seen = ++_walksSincePrune >= PruneWalks ? new HashSet<uint>(count) : null;
	}

	/// <summary>Whether this row differs from what its address last stated. Rows in order, each once.</summary>
	public bool Changed(int row)
	{
		int at = row * Stride;
		uint[] words = _filling;
		uint address = words[at + AddressWord];
		_seen?.Add(address);

		int match = -1;
		int end = Math.Min(_cursor + Lookahead, _previousCount);
		for (int candidate = _cursor; candidate < end; ++candidate)
		{
			if (_previous[(candidate * Stride) + AddressWord] == address)
			{
				match = candidate;
				break;
			}
		}

		if (match >= 0)
		{
			_cursor = match + 1;
			int was = match * Stride;
			ReadOnlySpan<uint> now = words;
			ReadOnlySpan<uint> before = _previous;
			if (now.Slice(at, IdentityWords).SequenceEqual(before.Slice(was, IdentityWords)) &&
				now.Slice(at + LookFirst, LookWords).SequenceEqual(before.Slice(was + LookFirst, LookWords)))
			{
				return false;
			}
		}

		var stated = (words[at], words[at + 1], words[at + 2],
			words[at + 3], words[at + 4], words[at + 5], words[at + 6],
			words[at + 7], words[at + 9], words[at + 10], words[at + 11]);

		// The look words too, highlight and appearance, so a walk that skips an unchanged entity
		// outright (GameEntities' still-adopted fast path) cannot miss a targeting outline or a fade
		// that arrives without the placement moving.
		var look = (words[at + 20], words[at + 21], words[at + 22],
			words[at + 23], words[at + 24], words[at + 25], words[at + 26],
			words[at + 27], words[at + 28]);
		bool changed = !_stated.TryGetValue(address, out var last) || !last.Equals(stated) ||
			!_look.TryGetValue(address, out var lastLook) || !lastLook.Equals(look);
		if (changed)
		{
			_stated[address] = stated;
			_look[address] = look;
		}

		return changed;
	}

	/// <summary>The walk's rows have been read: they are the next walk's previous rows.</summary>
	public void EndWalk(int count)
	{
		(_filling, _previous) = (_previous, _filling);
		_previousCount = count;
		if (_seen is null)
		{
			return;
		}

		_walksSincePrune = 0;
		var gone = new List<uint>();
		foreach (uint address in _stated.Keys)
		{
			if (!_seen.Contains(address))
			{
				gone.Add(address);
			}
		}

		foreach (uint address in gone)
		{
			_stated.Remove(address);
			_look.Remove(address);
		}

		_seen = null;
	}
}
