using System;
using System.Collections.Generic;

namespace Refii.Entities;

/// <summary>
/// The entity walk's skip verdicts, kept from one walk to the next: an entity the walk last
/// answered with one of its three skip paths (nothing to draw; still and adopted or batched;
/// still on its own node) is answered from here while its statement and everything the path
/// read stay as they were, without re-asking the lookups the path is made of.
/// </summary>
/// <remarks>
/// <para>
/// WHAT A SKIP PATH LEAVES BEHIND is what this keeps. The walk rebuilds three per-walk sets: the
/// owned entities (Retire keeps their nodes), the drawn records (the twin reconcile hides the
/// region's copy of each) and the drawn model keys (the record-less twins hidden by place). An
/// entity answered here does not write into them; it is marked present for the walk instead, and
/// those readers ask <see cref="Owns"/> and <see cref="AddDrawn"/> for the half the walk did not
/// write. Retire runs every walk, so ownership is a stamp and a count; the drawn sets are read
/// only by the reconcile and the debug channel, so they are filled on demand.
/// </para>
/// <para>
/// WHAT CAN UNDO A VERDICT. The statement (`Changed`: every placement and look word), the
/// entity's unique id, its reconcile cadence and whether it is a principal are checked by the
/// feed on every hit; the feed's own path rewrites or drops the verdict whenever it runs for the
/// entity. What changes it from outside that path is handed in: a rig built later
/// (<see cref="Invalidate"/>), a parent bone taken or let go, a lever that changes the skip
/// paths themselves (<see cref="Clear"/>). An entity absent from a walk loses its verdict.
/// </para>
/// <para>
/// ONE LOOKUP A ROW IS THE COST THIS IS HERE TO LOSE. The guest's list keeps its order, so the slot
/// a row found last walk is the slot it finds this walk: <see cref="TryHit(uint, ulong, int, out Kind)"/>
/// tries that slot first, and asks the table only when the row holds someone else.
/// </para>
/// </remarks>
public sealed class Engine_EntitySkipIndex
{
	/// <summary>Which of the walk's skip paths the verdict stands for.</summary>
	public enum Kind : byte
	{
		/// <summary>No model, no composite parts, no mesh records: counted, never owned.</summary>
		Nothing,

		/// <summary>Still and married to a static twin: owned; the twin is the drawable.</summary>
		Adopted,

		/// <summary>Still and drawn from an entity batch: owned, its record and model key drawn.</summary>
		Batched,

		/// <summary>Still on its own node, with nothing to pose or hang: owned, drawn unless married.</summary>
		Still,

		/// <summary>
		/// A rigged prop (a door, a sign, a lamp with dummy bones) whose clips have settled, or (the
		/// caller's tag) one out of view: owned and drawn as <see cref="Still"/>, and its pose gate
		/// still asked by the walk, which writes no bones for it on any walk this verdict answers.
		/// </summary>
		RigProp,
	}

	/// <summary>
	/// The walks a <see cref="Kind.RigProp"/> verdict lasts at most: the pose gate's settled-prop
	/// check (GameEntities, `Due(address, 6)`), the walk a settled prop's clips are read again.
	/// </summary>
	public const int RigPropRecheckTicks = 6;

	/// <summary>The empty path's own re-read, in reconcile periods: `Due(address, ReconcileTicks * 8)`.</summary>
	public const int NothingRecheckPeriods = 8;

	/// <summary>
	/// The cadence a held verdict of <paramref name="kind"/> is re-asked on, in walks: the walk its
	/// own path would next do something other than skip. The empty path re-reads the guest only on
	/// its longer cadence, the still paths on the reconcile (times `entity_still_reconcile_scale`).
	/// A <see cref="Kind.RigProp"/> verdict's is its holder's (GameEntities.Android.cs). Pure.
	/// </summary>
	public static int RecheckTicks(Kind kind, int reconcileTicks, int stillScale) => kind switch
	{
		// The empty path re-read the guest only on walks due on both its own cadence and the scaled reconcile
		// (one phase per address), so every lcm of the two.
		Kind.Nothing => reconcileTicks * Lcm(NothingRecheckPeriods, Math.Max(1, stillScale)),
		_ => reconcileTicks * stillScale,
	};

	private static int Lcm(int a, int b)
	{
		int x = a, y = b;
		while (y != 0)
		{
			(x, y) = (y, x % y);
		}

		return a / x * b;
	}

	private struct Entry
	{
		public uint Address;
		public ulong UniqueId;
		public uint Record;
		public string? Key;

		/// <summary>More model keys the path drew (a multi-mesh appearance's parts), or null.</summary>
		public string[]? Keys;

		/// <summary>The caller's word about the verdict (GameEntities: what a hit replays); 0 for none.</summary>
		public int Tag;
		public int Walk;
		public Kind Kind;

		/// <summary>This walk's presence came from a hit, so the walk's own sets do not hold it.</summary>
		public bool Hit;

		/// <summary>Something outside the feed's path changed what the verdict read; no more hits.</summary>
		public bool Stale;

		/// <summary>The slot holds a verdict.</summary>
		public bool Held;
	}

	private Entry[] _slots = new Entry[256];

	/// <summary>Slots ever used; those below this are held or on <see cref="_free"/>.</summary>
	private int _slotCount;

	private readonly Stack<int> _free = new();

	private readonly Dictionary<uint, int> _slotOf = new();

	/// <summary>The slot each row found last, plus one; zero for none.</summary>
	private int[] _rowSlots = new int[256];

	private readonly List<int> _absent = new();

	private int _walk;

	/// <summary>Entries stamped present this walk.</summary>
	private int _present;

	private bool _drawnAdded;

	/// <summary>Verdicts held.</summary>
	public int Count => _slotOf.Count;

	/// <summary>Entities this walk answered from the index.</summary>
	public int Hits { get; private set; }

	/// <summary>Of this walk's hits, the distinct entities the feed owns: the walk's own set lacks them.</summary>
	public int OwnedHits { get; private set; }

	/// <summary>Starts a walk: the previous walk's presence no longer counts.</summary>
	public void BeginWalk()
	{
		++_walk;
		_present = 0;
		Hits = 0;
		OwnedHits = 0;
		_drawnAdded = false;
	}

	/// <summary>
	/// The verdict held for <paramref name="address"/> under <paramref name="uniqueId"/>, marked
	/// present for this walk; false when none is held (or it went stale), and the walk's own path
	/// decides. The caller has already checked what every skip path checks of the statement.
	/// </summary>
	public bool TryHit(uint address, ulong uniqueId, out Kind kind) =>
		TryHit(address, uniqueId, -1, out kind);

	/// <summary>The same, for the entity at <paramref name="row"/> of this walk's list.</summary>
	public bool TryHit(uint address, ulong uniqueId, int row, out Kind kind)
	{
		if (!TryFind(address, uniqueId, row, out kind, out _))
		{
			return false;
		}

		Take();
		return true;
	}

	/// <summary>The slot <see cref="TryFind"/> last found, for <see cref="Take"/>; -1 for none.</summary>
	private int _found = -1;

	/// <summary>
	/// The verdict held for the entity at <paramref name="row"/>, and its <paramref name="tag"/>, NOT
	/// yet marked present: the caller checks what the verdict's kind asks of this walk first, then
	/// <see cref="Take"/>s it, or lets its own path decide (which rewrites or drops the verdict).
	/// </summary>
	public bool TryFind(uint address, ulong uniqueId, int row, out Kind kind, out int tag)
	{
		_found = -1;
		tag = 0;
		int slot = -1;
		if (row >= 0)
		{
			if (row >= _rowSlots.Length)
			{
				Array.Resize(ref _rowSlots, Math.Max(row + 1, _rowSlots.Length * 2));
			}

			slot = _rowSlots[row] - 1;
		}

		if (slot < 0 || !_slots[slot].Held || _slots[slot].Address != address)
		{
			if (!_slotOf.TryGetValue(address, out slot))
			{
				kind = default;
				return false;
			}

			if (row >= 0)
			{
				_rowSlots[row] = slot + 1;
			}
		}

		ref Entry found = ref _slots[slot];
		if (found.Stale || found.UniqueId != uniqueId)
		{
			kind = default;
			return false;
		}

		kind = found.Kind;
		tag = found.Tag;
		_found = slot;
		return true;
	}

	/// <summary>Answers the walk from the verdict <see cref="TryFind"/> just found: a hit, marked present.</summary>
	public void Take()
	{
		int slot = _found;
		_found = -1;
		if (slot < 0)
		{
			return;
		}

		ref Entry entry = ref _slots[slot];
		Kind kind = entry.Kind;
		++Hits;
		if (entry.Walk != _walk)
		{
			entry.Walk = _walk;
			entry.Hit = true;
			++_present;
			if (kind != Kind.Nothing)
			{
				++OwnedHits;
			}
		}
	}

	/// <summary>
	/// The walk's own path just took a skip: the verdict, and what that path added to the drawn
	/// sets (<paramref name="record"/> 0 and <paramref name="key"/> null for nothing).
	/// </summary>
	public void Park(uint address, ulong uniqueId, Kind kind, uint record, string? key) =>
		Park(address, uniqueId, kind, record, key, null, 0);

	/// <summary>
	/// The same, with the further model keys the path drew (<paramref name="keys"/>) and the caller's
	/// <paramref name="tag"/>, handed back by <see cref="TryFind"/>.
	/// </summary>
	public void Park(uint address, ulong uniqueId, Kind kind, uint record, string? key, string[]? keys, int tag)
	{
		if (!_slotOf.TryGetValue(address, out int slot))
		{
			slot = _free.Count != 0 ? _free.Pop() : _slotCount++;
			if (slot == _slots.Length)
			{
				Array.Resize(ref _slots, _slots.Length * 2);
			}

			_slotOf[address] = slot;
			++_present;
		}
		else
		{
			ref Entry held = ref _slots[slot];
			if (held.Walk != _walk)
			{
				++_present;
			}
			else if (held.Hit && held.Kind != Kind.Nothing)
			{
				// The same address twice in one walk, the first answered here: the walk's own set holds
				// it now.
				--OwnedHits;
			}
		}

		_slots[slot] = new Entry
		{
			Address = address,
			UniqueId = uniqueId,
			Record = record,
			Key = key,
			Keys = keys,
			Tag = tag,
			Walk = _walk,
			Kind = kind,
			Held = true,
		};
	}

	/// <summary>The walk's own path went past every skip: no verdict for it.</summary>
	public void Unpark(uint address)
	{
		if (!_slotOf.TryGetValue(address, out int slot))
		{
			return;
		}

		Entry old = _slots[slot];
		Drop(address, slot);
		if (old.Walk != _walk)
		{
			return;
		}

		--_present;
		if (old.Hit && old.Kind != Kind.Nothing)
		{
			--OwnedHits;
		}
	}

	private void Drop(uint address, int slot)
	{
		if (_found == slot)
		{
			_found = -1;
		}

		_slotOf.Remove(address);
		_slots[slot] = default;
		_free.Push(slot);
	}

	/// <summary>
	/// Something the verdict read changed outside the feed's path (a rig built, a parent taken):
	/// the next walk asks the path again. This walk's presence stands; it was decided before.
	/// </summary>
	public void Invalidate(uint address)
	{
		if (_slotOf.TryGetValue(address, out int slot))
		{
			_slots[slot].Stale = true;
		}
	}

	/// <summary>Whether this walk answered <paramref name="address"/> from here as an entity the feed owns.</summary>
	public bool Owns(uint address)
	{
		if (!_slotOf.TryGetValue(address, out int slot))
		{
			return false;
		}

		ref Entry entry = ref _slots[slot];
		return entry.Walk == _walk && entry.Hit && entry.Kind != Kind.Nothing;
	}

	/// <summary>
	/// Adds this walk's hits to the walk's drawn records and model keys, once per walk: what their
	/// skip paths would have added. The order of the keys is not the walk's; their reader is a set
	/// operation (Engine_StaticTwins.HideAtPlaces).
	/// </summary>
	public void AddDrawn(HashSet<uint> records, List<(uint Address, string ModelKey)> keys)
	{
		if (_drawnAdded)
		{
			return;
		}

		_drawnAdded = true;
		for (int slot = 0; slot < _slotCount; ++slot)
		{
			ref Entry entry = ref _slots[slot];
			if (!entry.Held || entry.Walk != _walk || !entry.Hit)
			{
				continue;
			}

			if (entry.Record != 0u)
			{
				records.Add(entry.Record);
			}

			if (entry.Key is { } key)
			{
				keys.Add((entry.Address, key));
			}

			if (entry.Keys is { } more)
			{
				foreach (string part in more)
				{
					keys.Add((entry.Address, part));
				}
			}
		}
	}

	/// <summary>Ends a walk: an entity it did not report keeps no verdict.</summary>
	public void EndWalk()
	{
		if (_present == _slotOf.Count)
		{
			return;
		}

		_absent.Clear();
		for (int slot = 0; slot < _slotCount; ++slot)
		{
			if (_slots[slot].Held && _slots[slot].Walk != _walk)
			{
				_absent.Add(slot);
			}
		}

		foreach (int slot in _absent)
		{
			Drop(_slots[slot].Address, slot);
		}
	}

	/// <summary>Drops every verdict: the skip paths themselves changed.</summary>
	public void Clear()
	{
		Array.Clear(_slots, 0, _slotCount);
		Array.Clear(_rowSlots);
		_slotCount = 0;
		_found = -1;
		_free.Clear();
		_slotOf.Clear();
		_present = 0;
		Hits = 0;
		OwnedHits = 0;
		_drawnAdded = false;
	}
}
