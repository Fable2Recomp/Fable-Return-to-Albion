using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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
	}

	private struct Entry
	{
		public ulong UniqueId;
		public uint Record;
		public string? Key;
		public int Walk;
		public Kind Kind;

		/// <summary>This walk's presence came from a hit, so the walk's own sets do not hold it.</summary>
		public bool Hit;

		/// <summary>Something outside the feed's path changed what the verdict read; no more hits.</summary>
		public bool Stale;
	}

	private readonly Dictionary<uint, Entry> _entries = new();

	private readonly List<uint> _absent = new();

	private int _walk;

	/// <summary>Entries stamped present this walk.</summary>
	private int _present;

	private bool _drawnAdded;

	/// <summary>Verdicts held.</summary>
	public int Count => _entries.Count;

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
	public bool TryHit(uint address, ulong uniqueId, out Kind kind)
	{
		ref Entry entry = ref CollectionsMarshal.GetValueRefOrNullRef(_entries, address);
		if (Unsafe.IsNullRef(ref entry) || entry.Stale || entry.UniqueId != uniqueId)
		{
			kind = default;
			return false;
		}

		kind = entry.Kind;
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

		return true;
	}

	/// <summary>
	/// The walk's own path just took a skip: the verdict, and what that path added to the drawn
	/// sets (<paramref name="record"/> 0 and <paramref name="key"/> null for nothing).
	/// </summary>
	public void Park(uint address, ulong uniqueId, Kind kind, uint record, string? key)
	{
		ref Entry entry = ref CollectionsMarshal.GetValueRefOrAddDefault(_entries, address, out bool exists);
		if (!exists || entry.Walk != _walk)
		{
			++_present;
		}
		else if (entry.Hit && entry.Kind != Kind.Nothing)
		{
			// The same address twice in one walk, the first answered here: the walk's own set holds
			// it now.
			--OwnedHits;
		}

		entry = new Entry
		{
			UniqueId = uniqueId,
			Record = record,
			Key = key,
			Walk = _walk,
			Kind = kind,
		};
	}

	/// <summary>The walk's own path went past every skip: no verdict for it.</summary>
	public void Unpark(uint address)
	{
		if (!_entries.Remove(address, out Entry old) || old.Walk != _walk)
		{
			return;
		}

		--_present;
		if (old.Hit && old.Kind != Kind.Nothing)
		{
			--OwnedHits;
		}
	}

	/// <summary>
	/// Something the verdict read changed outside the feed's path (a rig built, a parent taken):
	/// the next walk asks the path again. This walk's presence stands; it was decided before.
	/// </summary>
	public void Invalidate(uint address)
	{
		ref Entry entry = ref CollectionsMarshal.GetValueRefOrNullRef(_entries, address);
		if (!Unsafe.IsNullRef(ref entry))
		{
			entry.Stale = true;
		}
	}

	/// <summary>Whether this walk answered <paramref name="address"/> from here as an entity the feed owns.</summary>
	public bool Owns(uint address)
	{
		ref Entry entry = ref CollectionsMarshal.GetValueRefOrNullRef(_entries, address);
		return !Unsafe.IsNullRef(ref entry) && entry.Walk == _walk && entry.Hit && entry.Kind != Kind.Nothing;
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
		foreach ((uint address, Entry entry) in _entries)
		{
			if (entry.Walk != _walk || !entry.Hit)
			{
				continue;
			}

			if (entry.Record != 0u)
			{
				records.Add(entry.Record);
			}

			if (entry.Key is { } key)
			{
				keys.Add((address, key));
			}
		}
	}

	/// <summary>Ends a walk: an entity it did not report keeps no verdict.</summary>
	public void EndWalk()
	{
		if (_present == _entries.Count)
		{
			return;
		}

		_absent.Clear();
		foreach ((uint address, Entry entry) in _entries)
		{
			if (entry.Walk != _walk)
			{
				_absent.Add(address);
			}
		}

		foreach (uint address in _absent)
		{
			_entries.Remove(address);
		}
	}

	/// <summary>Drops every verdict: the skip paths themselves changed.</summary>
	public void Clear()
	{
		_entries.Clear();
		_present = 0;
		Hits = 0;
		OwnedHits = 0;
		_drawnAdded = false;
	}
}
